using MemRelief.Core.Contracts;

namespace MemRelief.Core.Releaser;

/// <summary>打开存活进程的结果分类：打开成功 / 已消失（pid 失效，含一切非受拒打开失败）/ 打开受拒。</summary>
internal enum LiveOpenKind
{
    Opened,
    Vanished,
    Denied,
}

/// <summary>
/// 执行期进程通道工厂：按 pid 打开存活进程会话。打开失败二分——受拒（Denied，错误码 5 等）/
/// 已消失（Vanished，pid 失效及其余非受拒失败）。
/// 实现侧为 Win32 薄通道（Win32ProcessChannel，覆盖率豁免）；合成单测以假实现驱动全部分支。
/// </summary>
internal interface ILiveProcessOpener
{
    ILiveProcess? TryOpen(int pid, out LiveOpenKind kind, out int win32Error);
}

/// <summary>
/// 执行期打开的存活进程句柄（会话域：一次 Execute 内从打开存活到用毕关闭）。
/// 身份校验（进程名+创建时间对快照）、优雅关闭投递、退出等待、强制结束的机械通道。
/// 实现侧为 Win32 薄通道（Win32LiveProcess，覆盖率豁免）；合成单测以假实现驱动全部分支。
/// </summary>
internal interface ILiveProcess : IDisposable
{
    int Pid { get; }

    /// <summary>
    /// 身份校验（PRD F3-2）：进程名（OrdinalIgnoreCase）与创建时间（FILETIME 精确值）均与快照一致才 true。
    /// 任一侧不可读（含快照创建时间哨兵 DateTime.MinValue，data-contracts §2 T-01 裁决②）→ false
    /// （不可判 = 不可杀，fail-closed，上层映射 IdentityChanged）。
    /// </summary>
    bool IdentityMatches(ProcessSnapshot snapshot);

    /// <summary>
    /// 执行期窗口自查（data-contracts §2 ③.s4 裁决⑦）：顶层可见窗口句柄（复用口径 #4 的
    /// IsVisibleCandidate 谓词：WS_VISIBLE 且非工具窗且非 DWM cloaked）。
    /// null = 自查失败（按有窗口走优雅路径兜底）；空集 = 确认无窗口（跳过优雅直杀）。
    /// </summary>
    IReadOnlyList<nint>? CollectTopLevelWindows();

    /// <summary>向窗口投递 WM_CLOSE（优雅关闭请求；不等待）。</summary>
    void PostClose(nint hwnd);

    /// <summary>等待进程退出至多 given 毫秒：true = 已退出（含提前退出），false = 超时仍存活。</summary>
    bool WaitExit(int milliseconds);

    /// <summary>强制结束：null = 成功；否则 Win32 错误码（5=拒绝访问，其余经上层存活复核实时的归类）。</summary>
    int? Terminate();
}
