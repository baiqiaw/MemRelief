using MemRelief.Core.Contracts;
using MemRelief.Core.Releaser;

namespace MemRelief.Core.Tests.Releaser;

// 合成通道假实现：按 pid 配置打开结局/身份匹配/窗口面/退出时机/强杀结局，
// 驱动 ProcessReleaser 全部分支（Win32 真通道薄，覆盖率豁免；真机路径另有 TestProcs 集成测试）。
internal sealed class FakeLiveProcess : ILiveProcess
{
    /// <summary>跨进程共享调用日志（时序断言；静态域，各测试自行 Reset）。</summary>
    public static readonly List<string> CallLog = new();

    public static void ResetLog()
    {
        lock (CallLog)
        {
            CallLog.Clear();
        }
    }

    public static void Log(int pid, string op)
    {
        lock (CallLog)
        {
            CallLog.Add($"{op}:{pid}");
        }
    }

    public int Pid { get; }
    public bool IdentityMatch { get; set; } = true;

    /// <summary>窗口自查结果：null=自查失败（优雅兜底语义）；空集=确认无窗口；非空=待投递句柄。</summary>
    public IReadOnlyList<nint>? Windows { get; set; } = Array.Empty<nint>();

    /// <summary>窗口自查抛异常（意外异常兜底路径触发器）。</summary>
    public bool ThrowOnWindows { get; set; }

    public List<nint> ClosePostedTo { get; } = new();
    public int WaitExitProbeCalls { get; private set; }
    public int TerminateCalls { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>WaitExit 探活第 N 次调用返回已退出（int.MaxValue = 永不退出）。</summary>
    public int ExitOnProbe { get; set; } = int.MaxValue;

    /// <summary>Tenate 返回的 Win32 错误码（null = 成功）。</summary>
    public int? TerminateError { get; set; }

    public FakeLiveProcess(int pid) => Pid = pid;

    public bool IdentityMatches(ProcessSnapshot snapshot)
    {
        Log(Pid, "identity");
        return IdentityMatch;
    }

    public IReadOnlyList<nint>? CollectTopLevelWindows()
    {
        Log(Pid, "windows");
        if (ThrowOnWindows)
        {
            throw new InvalidOperationException("窗口自查注入异常（合成兜底路径触发）");
        }
        return Windows;
    }

    public void PostClose(nint hwnd)
    {
        Log(Pid, "close");
        ClosePostedTo.Add(hwnd);
    }

    public bool WaitExit(int milliseconds)
    {
        WaitExitProbeCalls++;
        Log(Pid, "wait");
        return WaitExitProbeCalls >= ExitOnProbe;
    }

    public int? Terminate()
    {
        TerminateCalls++;
        Log(Pid, "terminate");
        return TerminateError;
    }

    public void Dispose()
    {
        Disposed = true;
        Log(Pid, "dispose");
    }
}

internal sealed class FakeProcessOpener : ILiveProcessOpener
{
    private readonly Dictionary<int, Func<FakeLiveProcess?>> _factories = new();

    /// <summary>
    /// 登记存活进程（默认身份匹配、确认无窗口、杀必成功）。
    /// 窗口自查失败语义不在本工厂表达（省缺参数与显式 null 不可区分）：取返回实例后置 Windows=null。
    /// </summary>
    public FakeLiveProcess AddLive(
        int pid,
        bool identityMatch = true,
        IReadOnlyList<nint>? windows = null,
        int? terminateError = null)
    {
        var live = new FakeLiveProcess(pid)
        {
            IdentityMatch = identityMatch,
            Windows = windows ?? Array.Empty<nint>(),
            TerminateError = terminateError,
        };
        _factories[pid] = () => live;
        return live;
    }

    /// <summary>登记打开受拒（携带 Win32 错误码，通常 5）。</summary>
    public void AddDenied(int pid, int error = 5)
    {
        DeniedErrors[pid] = error;
        _factories[pid] = () => null;
    }

    /// <summary>登记已消失（pid 失效：打开即非受拒失败）。</summary>
    public void AddVanished(int pid) => _factories[pid] = () => null;

    public Dictionary<int, int> DeniedErrors { get; } = new();

    public ILiveProcess? TryOpen(int pid, out LiveOpenKind kind, out int win32Error)
    {
        if (!_factories.TryGetValue(pid, out var factory))
        {
            // 未登记 = 打开失败且非受拒（与真实通道"pid 失效"同语义）
            kind = LiveOpenKind.Vanished;
            win32Error = 87;
            return null;
        }

        FakeLiveProcess.Log(pid, "open");
        var live = factory();
        if (live != null)
        {
            kind = LiveOpenKind.Opened;
            win32Error = 0;
            return live;
        }

        // 登记为受拒 → Denied（错误码取配置）；否则 Vanished
        kind = DeniedErrors.TryGetValue(pid, out var deniedError) ? LiveOpenKind.Denied : LiveOpenKind.Vanished;
        win32Error = deniedError;
        return null;
    }
}
