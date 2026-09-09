namespace MemRelief.Core.Scanner;

/// <summary>
/// 活动信号纯判定面（T-02，五口径 #4/#6/#7/#8/#10 的可单测部分）：
/// 窗口可见性谓词、TCP 状态过滤与表行解析、系统目录/UWP 前缀匹配、服务失败恢复动作解析、多服务归并。
/// 零 I/O 零状态；手写结构偏移的解析函数收口于此（构造缓冲单测承载，AC③"构造数据对测"）。
/// 口径出处：PRD 判定信号技术口径表 #4/#6/#7/#8/#10；消费语义见 RulesEngine（null=保守兜底）。
/// </summary>
public static class SignalRules
{
    private const long WsVisible = 0x1000_0000;
    private const long WsExToolWindow = 0x0000_0080;

    /// <summary>MIB_TCP_STATE_ESTAB=5（SDK tcpmib.h；GetExtendedTcpTable 原始状态值直读）。</summary>
    private const int TcpStateEstablished = 5;

    /// <summary>口径 #4 谓词：计入 WS_VISIBLE、非 WS_EX_TOOLWINDOW、非 DWM cloaked（属主过滤在枚举侧按 pid 完成）。</summary>
    public static bool IsVisibleCandidate(long style, long exStyle, bool cloaked) =>
        (style & WsVisible) != 0 && (exStyle & WsExToolWindow) == 0 && !cloaked;

    /// <summary>口径 #6 过滤：仅 ESTABLISHED 计"活跃"（LISTEN/TIME_WAIT/SYN_* 不计；UDP 无连接语义不采集）。</summary>
    public static bool IsEstablished(int tcpState) => tcpState == TcpStateEstablished;

    /// <summary>
    /// 口径 #10 前缀匹配：大小写不敏感 + 分隔符边界（"C:\Windows" 不命中 "C:\WindowsExplorer\..."）。
    /// prefix 为已归一化完整路径（不带尾分隔符亦可，匹配时补边界判定）。
    /// </summary>
    public static bool PathMatchesPrefix(string? path, string prefix)
    {
        if (path is null || prefix.Length == 0 || path.Length < prefix.Length)
        {
            return false;
        }
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // 边界：path 恰等于 prefix，或 prefix 后紧跟分隔符（防 "C:\Windows" 命中 "C:\WindowsX"）
        return path.Length == prefix.Length || path[prefix.Length] is '\\' or '/';
    }

    /// <summary>口径 #10 批量：任一系统目录前缀命中即 true。</summary>
    public static bool IsUnderAnyPrefix(string? path, IReadOnlyList<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (PathMatchesPrefix(path, prefix))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 口径 #8 表行解析（T-02）：MIB_TCPTABLE_OWNER_PID 行序遍历（x64 行 24 字节：State@0/OwningPid@20），
    /// 仅 ESTABLISHED 且属候选 pid 计数。buffer 布局：dwNumEntries@0，行数组@4。
    /// </summary>
    internal static unsafe void CountEstablishedRows(byte* table, int bufferSize, IReadOnlySet<int> candidatePids, Dictionary<int, int> counts)
    {
        const int rowSize = 24;
        var entries = *(int*)table;
        if (entries < 0 || entries * rowSize + sizeof(int) > bufferSize)
        {
            return;    // 表头与缓冲尺寸不自洽（内核契约外形态）→ 丢弃本表，上层按零计数（保守方向不变）
        }
        var row = table + sizeof(int);
        for (var i = 0; i < entries; i++)
        {
            var state = *(int*)row;
            var pid = *(int*)(row + 20);
            if (candidatePids.Contains(pid) && IsEstablished(state))
            {
                counts[pid] = counts.TryGetValue(pid, out var current) ? current + 1 : 1;
            }
            row += rowSize;
        }
    }

    /// <summary>
    /// 口径 #8 失败恢复动作解析（T-02）：SERVICE_FAILURE_ACTIONS x64 布局
    /// dwResetPeriod@0/lpRebootMsg@8/lpCommand@16/cActions@24/lpsaActions 指针@32——数组本体在缓冲区后段，
    /// 须解指针读取（SDK um/winsvc.h；误作内联数组会恒判 false，"⚠️会被拉起"信号失灵）。
    /// lpsaActions=null（未配置动作）→ false；cActions 与数组不自洽（越界防御）→ false。
    /// </summary>
    internal static unsafe bool HasRestartAction(byte* failureActions, int bufferSize)
    {
        var actionCount = *(int*)(failureActions + 24);
        var actions = *(byte**)(failureActions + 32);
        if (actions is null || actionCount <= 0)
        {
            return false;
        }
        for (var i = 0; i < actionCount; i++)
        {
            // SC_ACTION x64 步长 8（Type int@0 + Delay uint@4）；越出宿主缓冲即停止（防御 cActions 与实际不符）
            if ((long)(actions - failureActions) + i * 8 + 8 > bufferSize)
            {
                return false;
            }
            if (*(int*)(actions + i * 8) == 1 /* SC_ACTION_RESTART */)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 同 pid 多服务归并（svchost 分组场景，T-02 裁决③）：ServiceName 取首个；
    /// RestartOnFailure 三级归并——任一配置重启 → true；否则任一读取失败 → null（合并层配 SignalFailure #8，
    /// 不塌缩为 false：false=「已核实未配置」，null 被吞即违反契约配对不变量）；全为 false 才 false。
    /// </summary>
    public static ServiceSignalInfo MergeServices(IReadOnlyList<ServiceSignalInfo> services)
    {
        if (services.Count == 0)
        {
            throw new ArgumentException("服务清单为空（调用方保证仅在命中时调用）", nameof(services));
        }
        bool? restart = services.Any(s => s.RestartOnFailure == true)
            ? true
            : services.Any(s => s.RestartOnFailure is null) ? null : false;
        return new ServiceSignalInfo(services[0].Name, restart);
    }
}
