using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;
using Xunit;

namespace MemRelief.Core.Tests.Scanner;

// T-02 活动信号采集器群单测：五口径（#4/#6/#7/#8/#10）的纯判定面——
// 窗口可见性谓词、TCP 状态过滤与表行解析（构造缓冲）、系统目录前缀匹配、失败恢复动作解析（构造缓冲）、
// 多服务归并（null 传播）、SignalInputs→SignalSet 合并（含全局失败去重与配对不变量）。
// 通道真机可用性由 ScannerIntegrationTests 冒烟承载。
public class SignalCollectorTests
{
    private const long WsVisible = 0x1000_0000;
    private const long WsExToolWindow = 0x0000_0080;

    // —— CASE：口径 #4 窗口可见性谓词（计入：WS_VISIBLE、非 TOOLWINDOW、非 cloaked、属主=目标进程）——

    [Theory]
    [InlineData(WsVisible, 0, false, true)]               // 正常可见窗口
    [InlineData(WsVisible, WsExToolWindow, false, false)] // 工具窗口不计
    [InlineData(WsVisible, 0, true, false)]               // cloaked（DWM 隐藏）不计
    [InlineData(0, 0, false, false)]                      // 无 WS_VISIBLE 不计
    public void 窗口可见性谓词_四分支(long style, long exStyle, bool cloaked, bool expected)
    {
        Assert.Equal(expected, SignalRules.IsVisibleCandidate(style, exStyle, cloaked));
    }

    // —— CASE：口径 #6 TCP 状态过滤（仅 ESTABLISHED 计活跃；LISTEN=2/SYN_SENT=3/SYN_RCVD=4/TIME_WAIT=11 不计）——

    [Theory]
    [InlineData(5, true)]    // MIB_TCP_STATE_ESTAB
    [InlineData(2, false)]   // LISTSEN
    [InlineData(3, false)]   // SYN_SENT
    [InlineData(4, false)]   // SYN_RCVD
    [InlineData(11, false)]  // TIME_WAIT
    public void tcp状态过滤_仅established计活跃(int state, bool expected)
    {
        Assert.Equal(expected, SignalRules.IsEstablished(state));
    }

    // —— CASE：口径 #6 表行解析（构造 MIB_TCPTABLE_OWNER_PID 缓冲：表头 4 字节 + 行 24 字节 state@0/pid@20）——

    [Fact]
    public unsafe void tcp表行解析_仅候选pid的established计数()
    {
        var candidates = new HashSet<int> { 1, 2 };
        // 行：[pid=1 EST][pid=1 LISTEN][pid=2 EST×2][pid=9 EST 不在候选]
        var rows = new (int pid, int state)[]
        {
            (1, 5), (1, 2), (2, 5), (2, 5), (9, 5),
        };
        var buffer = new byte[4 + rows.Length * 24];
        fixed (byte* p = buffer)
        {
            *(int*)p = rows.Length;
            for (var i = 0; i < rows.Length; i++)
            {
                var row = p + 4 + i * 24;
                *(int*)row = rows[i].state;
                *(int*)(row + 20) = rows[i].pid;
            }

            var counts = new Dictionary<int, int>();
            SignalRules.CountEstablishedRows(p, buffer.Length, candidates, counts);

            Assert.Equal(1, counts[1]);
            Assert.Equal(2, counts[2]);
            Assert.False(counts.ContainsKey(9));
        }
    }

    [Fact]
    public unsafe void tcp表行解析_表头与缓冲不自洽_丢弃不越界()
    {
        var buffer = new byte[4 + 24];
        fixed (byte* p = buffer)
        {
            *(int*)p = 999;   // entries 与缓冲尺寸矛盾（防御内核契约外形态）

            var counts = new Dictionary<int, int>();
            SignalRules.CountEstablishedRows(p, buffer.Length, new HashSet<int> { 1 }, counts);

            Assert.Empty(counts);
        }
    }

    // —— CASE：口径 #10 系统目录前缀匹配（大小写不敏感 + 分隔符边界）——

    [Fact]
    public void 系统目录匹配_windir命中且大小写不敏感()
    {
        Assert.True(SignalRules.PathMatchesPrefix(@"C:\WINDOWS\system32\svchost.exe", @"C:\Windows"));
        Assert.True(SignalRules.PathMatchesPrefix(@"c:\windows\syswow64\tool.exe", @"C:\Windows\SysWOW64"));
    }

    [Fact]
    public void 系统目录匹配_分隔符边界_非子目录不命中()
    {
        Assert.False(SignalRules.PathMatchesPrefix(@"C:\WindowsExplorer\fake.exe", @"C:\Windows"));
        Assert.False(SignalRules.PathMatchesPrefix(@"D:\app\tool.exe", @"C:\Windows"));
    }

    [Fact]
    public void 系统目录匹配_路径null不命中()
    {
        Assert.False(SignalRules.PathMatchesPrefix(null, @"C:\Windows"));
    }

    // —— CASE：口径 #8 失败恢复动作解析（构造 SERVICE_FAILURE_ACTIONS 缓冲：头 40 字节 + SC_ACTION 数组在缓冲后段，
    //     lpsaActions 指针@32 指向数组——非内联，误作内联会恒判 false，T-02 cross-review Critical 修复项）——

    [Fact]
    public unsafe void 失败恢复解析_含restart动作_命中()
    {
        Assert.True(HasRestart(1, new[] { 1 }));                       // 单动作 restart
        Assert.True(HasRestart(2, new[] { 2, 1 }));                    // 多动作含 restart（reboot 在前）
    }

    [Fact]
    public unsafe void 失败恢复解析_未配置或无restart_不命中()
    {
        Assert.False(HasRestart(0, Array.Empty<int>()));                        // cActions=0（lpsaActions 可为 null）
        Assert.False(HasRestart(1, new[] { 2 }));                      // 配置了 reboot 但未配置 restart
        Assert.False(HasRestart(2, new[] { 2, 3 }));                   // 多动作均非 restart
    }

    [Fact]
    public unsafe void 失败恢复解析_lpsaActions为null_不命中不越界()
    {
        var buffer = new byte[40];
        fixed (byte* p = buffer)
        {
            *(int*)(p + 24) = 3;      // cActions 与 null 指针并存（契约允许，cActions 被忽略）
            *(nint*)(p + 32) = nint.Zero;

            Assert.False(SignalRules.HasRestartAction(p, buffer.Length));
        }
    }

    /// <summary>构造 SERVICE_FAILURE_ACTIONS 缓冲（头 40 字节：cActions@24/lpsaActions@32 指向缓冲后段动作数组）。</summary>
    private static unsafe bool HasRestart(int cActions, params int[] actionTypes)
    {
        var buffer = new byte[40 + Math.Max(actionTypes.Length, 1) * 8];
        fixed (byte* p = buffer)
        {
            *(int*)(p + 24) = cActions;
            if (cActions > 0)
            {
                var actions = p + 40;
                *(nint*)(p + 32) = (nint)actions;
                for (var i = 0; i < cActions; i++)
                {
                    *(int*)(actions + i * 8) = actionTypes[i];
                }
            }
            return SignalRules.HasRestartAction(p, buffer.Length);
        }
    }

    // —— CASE：口径 #8 多服务归并（svchost 分组；null 传播防"不可读塌缩为已核实不重启"）——

    [Fact]
    public void 多服务归并_任一配置重启即true_名字取首个()
    {
        var merged = SignalRules.MergeServices(
            new[] { new ServiceSignalInfo("svc_a", false), new ServiceSignalInfo("svc_b", true) });

        Assert.Equal("svc_a", merged.Name);
        Assert.True(merged.RestartOnFailure);
    }

    [Fact]
    public void 多服务归并_无true但有读取失败_null传播不塌缩false()
    {
        var merged = SignalRules.MergeServices(
            new[] { new ServiceSignalInfo("svc_a", null), new ServiceSignalInfo("svc_b", false) });

        Assert.Null(merged.RestartOnFailure);
    }

    [Fact]
    public void 多服务归并_全读取失败_null传播()
    {
        var merged = SignalRules.MergeServices(
            new[] { new ServiceSignalInfo("svc_a", null), new ServiceSignalInfo("svc_b", null) });

        Assert.Null(merged.RestartOnFailure);
    }

    [Fact]
    public void 多服务归并_全未配置_false()
    {
        var merged = SignalRules.MergeServices(
            new[] { new ServiceSignalInfo("svc_a", false), new ServiceSignalInfo("svc_b", false) });

        Assert.False(merged.RestartOnFailure);
    }

    // —— CASE：SignalInputs → SignalSet 合并（配对不变量：per-pid 失败伴随 SignalFailure；全局失败由 Assemble 去重登记）——

    private static RawProcess Row(
        int pid, string? path = @"C:\app\tool.exe", ProcessOpenOutcome open = ProcessOpenOutcome.Opened) =>
        new(pid, 4, "tool.exe", open,
            path is null ? ProcessField<string?>.Fail("不可读") : ProcessField<string?>.Ok(path),
            ProcessField<DateTime?>.Ok(DateTime.UtcNow), ProcessField<long?>.Ok(100), ProcessField<string?>.Ok("me"),
            CpuStart: ProcessField<double?>.Ok(0.1), CommandLine: null);

    private static SignalInputs Inputs(
        IReadOnlySet<int>? visiblePids = null,
        IReadOnlyDictionary<int, int>? tcp = null,
        IReadOnlyDictionary<int, ServiceSignalInfo>? services = null,
        IReadOnlyList<string>? prefixes = null,
        string? uwpPrefix = null,
        IReadOnlyDictionary<int, double?>? cpu = null,
        bool windowFailed = false, bool tcpFailed = false, bool serviceFailed = false, bool dirFailed = false) =>
        new(
            visiblePids ?? new HashSet<int>(),
            windowFailed,
            tcp ?? new Dictionary<int, int>(),
            tcpFailed,
            services ?? new Dictionary<int, ServiceSignalInfo>(),
            serviceFailed,
            prefixes ?? new[] { @"C:\Windows" },
            uwpPrefix,
            dirFailed,
            // 默认 pid1 已采得差分（0.1s，未达阈值），避免与被测口径无关的 #7 failure 噪音
            cpu ?? new Dictionary<int, double?> { [1] = 0.1 });

    [Fact]
    public void 合并_窗口与tcp与cpu全命中_字段正确填充()
    {
        var row = Row(1);
        var inputs = Inputs(
            visiblePids: new HashSet<int> { 1 },
            tcp: new Dictionary<int, int> { [1] = 3 },
            cpu: new Dictionary<int, double?> { [1] = 1.5 });

        var failures = new List<SignalFailure>();
        var signals = SnapshotAssembler.MergeSignals(row, inputs, failures);

        Assert.Empty(failures);
        Assert.True(signals.HasVisibleWindow);
        Assert.Equal(3, signals.TcpEstablishedCount);
        Assert.Equal(1.5, signals.CpuDeltaSeconds);
        Assert.Null(signals.ServiceName);
        Assert.False(signals.IsUwpPackage);
        Assert.False(signals.IsSystemDirectory);
    }

    [Fact]
    public void 合并_窗口缺席_false_无failure()
    {
        var failures = new List<SignalFailure>();
        var signals = SnapshotAssembler.MergeSignals(Row(1), Inputs(), failures);

        Assert.False(signals.HasVisibleWindow);
        Assert.Empty(failures);
    }

    [Fact]
    public void 合并_cpu不可得_null伴随failure7_保守兜底()
    {
        var failures = new List<SignalFailure>();
        var signals = SnapshotAssembler.MergeSignals(
            Row(1), Inputs(cpu: new Dictionary<int, double?> { [1] = null }), failures);

        Assert.Null(signals.CpuDeltaSeconds);
        var failure = Assert.Single(failures);
        Assert.Equal(7, failure.SignalId);
        Assert.Equal(1, failure.Pid);
        Assert.Equal(FailureKind.Unreadable, failure.Kind);
    }

    [Fact]
    public void 合并_服务配置查询失败_RestartOnFailure_null伴随failure8()
    {
        var failures = new List<SignalFailure>();
        var inputs = Inputs(services: new Dictionary<int, ServiceSignalInfo> { [1] = new("svc_a", null) });
        var signals = SnapshotAssembler.MergeSignals(Row(1), inputs, failures);

        Assert.Equal("svc_a", signals.ServiceName);
        Assert.Null(signals.ServiceRestartOnFailure);
        var failure = Assert.Single(failures);
        Assert.Equal(8, failure.SignalId);
        Assert.Equal(1, failure.Pid);
    }

    [Fact]
    public void 合并_非服务进程_无failure无字段()
    {
        var failures = new List<SignalFailure>();
        var signals = SnapshotAssembler.MergeSignals(Row(1), Inputs(), failures);

        Assert.Null(signals.ServiceName);
        Assert.Null(signals.ServiceRestartOnFailure);
        Assert.Empty(failures);
    }

    [Fact]
    public void 合并_系统目录命中_布尔填充_windir外为false()
    {
        var sysSignals = SnapshotAssembler.MergeSignals(
            Row(1, path: @"C:\Windows\System32\svchost.exe"), Inputs(), new List<SignalFailure>());
        Assert.True(sysSignals.IsSystemDirectory);

        var appSignals = SnapshotAssembler.MergeSignals(
            Row(2, path: @"D:\apps\tool.exe"), Inputs(), new List<SignalFailure>());
        Assert.False(appSignals.IsSystemDirectory);
    }

    [Fact]
    public void 合并_Uwp路径_IsUwpPackage为真且不触发系统目录链路()
    {
        var row = Row(1, path: @"C:\Program Files\WindowsApps\Microsoft.Contoso_1.0\x64\App.exe");
        var signals = SnapshotAssembler.MergeSignals(
            row, Inputs(uwpPrefix: @"C:\Program Files\WindowsApps"), new List<SignalFailure>());

        Assert.True(signals.IsUwpPackage);
        // WindowsApps 不触发"系统目录→按微软处理→🚫"链路（v1.2 修订）：不命中即 false
        Assert.False(signals.IsSystemDirectory);
    }

    [Fact]
    public void 合并_路径不可读_系统目录null不加failure10_编号100已覆盖()
    {
        var failures = new List<SignalFailure>();
        var row = new RawProcess(1, 4, "x.exe", ProcessOpenOutcome.Opened,
            ProcessField<string?>.Fail("访问被拒"), ProcessField<DateTime?>.Ok(DateTime.UtcNow),
            ProcessField<long?>.Ok(0), ProcessField<string?>.Ok("me"),
            CpuStart: ProcessField<double?>.Fail("不可读"), CommandLine: null);

        var signals = SnapshotAssembler.MergeSignals(
            row,
            Inputs(cpu: new Dictionary<int, double?>()),   // 路径不可读行不带 CPU 断言，隔离被测口径
            failures);

        Assert.Null(signals.IsSystemDirectory);
        Assert.DoesNotContain(failures, f => f.SignalId == 10);
    }

    // —— CASE：Assemble 全局失败登记与去重（每口径整次扫描一条，Pid=null）——

    [Fact]
    public void assemble_窗口枚举全局失败_多行仅单条failure且全pid置null()
    {
        var rows = new[] { Row(1), Row(2) };
        var inputs = Inputs(windowFailed: true, cpu: new Dictionary<int, double?>());

        var result = SnapshotAssembler.Assemble(rows, inputs, DateTime.UtcNow, 100);

        var windowFailures = result.Failures.Where(f => f.SignalId == 4).ToList();
        var failure = Assert.Single(windowFailures);
        Assert.Null(failure.Pid);
        Assert.Equal(FailureKind.CollectorFailed, failure.Kind);
        Assert.All(result.Snapshots, s => Assert.Null(s.Signals.HasVisibleWindow));
    }

    [Fact]
    public void assemble_四通道全失败_各口径恰一条全局failure()
    {
        var rows = new[] { Row(1) };
        var inputs = Inputs(windowFailed: true, tcpFailed: true, serviceFailed: true, dirFailed: true,
            cpu: new Dictionary<int, double?>());

        var result = SnapshotAssembler.Assemble(rows, inputs, DateTime.UtcNow, 100);

        var globals = result.Failures.Where(f => f.Pid is null && f.Kind == FailureKind.CollectorFailed).ToList();
        Assert.Equal(new[] { 4, 6, 8, 10 }, globals.Select(f => f.SignalId).Order().ToArray());
    }
}
