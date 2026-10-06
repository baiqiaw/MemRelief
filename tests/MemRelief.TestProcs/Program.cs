using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

// MemRelief.TestProcs（T-20 孤儿测试进程构造器）——用法与场景矩阵见同目录 README.md。
// 构造规格锚点（PRD §3.2 前置构造规格）：父退出、私有提交 >50MB、无可见窗口、无 ESTABLISHED（默认形态）、
// 非服务、非 UWP、不在常驻名单、同可执行目录无其他存活进程；降级场景按参数正交开启。

return args switch
{
    ["child", .. var rest] => RunChild(rest),
    ["parent", .. var rest] => RunParent(rest),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        MemRelief.TestProcs — 孤儿测试进程构造器（T-20）

        用法：
          parent [--mem-mb N] [--cpu] [--established] [--window] [--ignore-close] [--out-pid <file>]
              启动 child → 等 child 就绪 → 本进程退出（child 成为孤儿）。
              --out-pid：首行=child pid。--window/--ignore-close 转发给 child。
          child  [--mem-mb N] [--cpu] [--established] [--window] [--ignore-close]
              挂起等待被外部终止；--mem-mb 0 = 仅挂起（同目录存活场景由活父直接启动本形态）。
              --window：顶层可见窗口+消息循环（默认 WM_CLOSE 关闭退出；--ignore-close 吞并关闭信号）。

        退出码：0 成功；2 就绪超时；1 参数错误（未识别/缺值/重复/空串值/值为 flag 名；WinExe 无控制台，错误详情不可见，用法见 README）；其他非零 = 运行时异常崩溃。
        """);
    return 1;
}

static int RunChild(string[] args)
{
    // 严格校验前置于一切解析与资源构造（issue #61）：未识别 token / 带值参数值为 flag 名 / 重复带值参数 → 码 1
    if (!ArgsAreValid(args, ArgSets.Flags, ArgSets.ChildValued))
    {
        return 1;
    }

    var memMb = GetInt(args, "--mem-mb", 60);
    var spinCpu = Has(args, "--cpu");
    var established = Has(args, "--established");
    var readyEvent = GetStr(args, "--ready-event");
    var windowed = Has(args, "--window");
    var ignoreClose = Has(args, "--ignore-close");

    // 私有提交 >50MB：托管数组存活即保持 commit charge（口径 #13 PrivateUsage）；0=仅挂起合法形态（同目录存活旁证，README 场景矩阵）
    if (IsMemMbInvalid(memMb))
    {
        return 1;   // 参数越界或缺值/非数值（WinExe 无控制台，退出码 1=参数错误；用法见 README）
    }

    // 空串值 ≠ 未提供：按参数错误退出（issue #55，与 --mem-mb 同口径；缺值/值为 flag 名已由 ArgsAreValid 前置拦截）
    if (Has(args, "--ready-event") && GetStr(args, "--ready-event") is not { Length: > 0 })
    {
        return 1;
    }

    if (memMb.Value > 0)
    {
        Hold.Memory = new byte[memMb.Value * 1024L * 1024L];
        Hold.Memory[0] = 0xAA;
        Hold.Memory[^1] = 0xBB;
    }

    if (spinCpu)
    {
        // CPU 差分场景（口径 #7）：双自旋线程保证窗口内差分 >1s
        for (var i = 0; i < 2; i++)
        {
            new Thread(() => { while (true) { } }) { IsBackground = true }.Start();
        }
    }

    if (established)
    {
        Hold.StartLoopbackEstablished();
    }

    if (windowed)
    {
        // 窗口先于就绪信号：EnumWindows 自查可见后再宣布就绪（R03 释放链路优雅路径载体）
        TestWindow.Show(ignoreClose);
    }

    if (readyEvent is { } name)
    {
        using var evt = EventWaitHandle.OpenExisting(name);
        evt.Set();   // 就绪信号：内存/连接/窗口已就位，父可退出造孤儿
    }

    if (windowed)
    {
        TestWindow.Pump();   // 消息循环：默认 WM_CLOSE → 销毁窗口退出（优雅成功路径）；--ignore-close 吞并（3s 转强杀路径）
        return 0;
    }

    Thread.Sleep(Timeout.Infinite);   // 挂起等待被外部终止（验收脚本 taskkill）
    return 0;
}

static int RunParent(string[] args)
{
    // 同 child 侧严格校验（issue #61）：前置于 child 启动，参数错误不产生 child
    if (!ArgsAreValid(args, ArgSets.Flags, ArgSets.ParentValued))
    {
        return 1;
    }

    var exe = Environment.ProcessPath;
    if (exe is null)
    {
        return 1;
    }
    var memMb = GetInt(args, "--mem-mb", 60);
    var spinCpu = Has(args, "--cpu");
    var established = Has(args, "--established");
    var windowed = Has(args, "--window");
    var ignoreClose = Has(args, "--ignore-close");
    var outPidPath = GetStr(args, "--out-pid");

    // 越界或缺值/非数值：参数错误直接退出，不启动 child（与 child 侧校验同口径；此前经 child 拒绝落 2，issue #44）
    if (IsMemMbInvalid(memMb))
    {
        return 1;
    }

    // 空串守卫（issue #55）：--out-pid 空串按参数错误退出，前置于 child 启动（缺值已由 ArgsAreValid 前置拦截）
    if (Has(args, "--out-pid") && GetStr(args, "--out-pid") is not { Length: > 0 })
    {
        return 1;
    }

    var readyName = $"MemRelief.TestProcs.Ready.{Guid.NewGuid():N}";
    using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);

    var child = StartChild(exe, memMb.Value, spinCpu, established, windowed, ignoreClose, readyName);

    // 泄漏防御：就绪等待/写出 pid 文件失败时必须清掉已启动的挂起 child，否则成为无 pid 记录的隐形孤儿
    try
    {
        if (!ready.WaitOne(10_000))
        {
            TryKill(child);
            return 2;
        }

        if (outPidPath is not null)
        {
            File.WriteAllLines(outPidPath, [child.Id.ToString()]);
        }
    }
    catch
    {
        TryKill(child);
        throw;
    }

    return 0;   // 父退出 → child 成为孤儿（父 PID 指向已退出进程）
}

static Process StartChild(string exe, int memMb, bool spinCpu, bool established, bool windowed,
    bool ignoreClose, string? readyName)
{
    var psi = new ProcessStartInfo(exe) { CreateNoWindow = true, UseShellExecute = false };
    psi.ArgumentList.Add("child");
    psi.ArgumentList.Add("--mem-mb");
    psi.ArgumentList.Add(memMb.ToString());
    if (spinCpu)
    {
        psi.ArgumentList.Add("--cpu");
    }
    if (established)
    {
        psi.ArgumentList.Add("--established");
    }
    if (windowed)
    {
        psi.ArgumentList.Add("--window");
    }
    if (ignoreClose)
    {
        psi.ArgumentList.Add("--ignore-close");
    }
    if (readyName is not null)
    {
        psi.ArgumentList.Add("--ready-event");
        psi.ArgumentList.Add(readyName);
    }
    return Process.Start(psi)!;
}

static void TryKill(Process process)
{
    try
    {
        process.Kill(entireProcessTree: false);
    }
    catch
    {
        // 已退出/已无权限：构造器清理尽力而为
    }
}

static bool Has(string[] args, string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

/// <summary>严格参数校验（issue #61）：所有 token 须可归类——已知 flag，或已知带值参数的值
/// （值另不得以 -- 开头：以 -- 开头视为缺值，防 flag 名作值被静默放行，如 --out-pid --cpu 假成功
/// 创建名为 "--cpu" 的文件）；未识别 token（拼错参数名、裸位置参数）一律参数错误，不再静默忽略；
/// 同名带值参数二次出现即拒绝（重复值逃逸校验：--mem-mb 0 --mem-mb abc 中 abc 放行即假成功，
/// cross-review 实测），flag 重复幂等无害不拒（与 #44/#55 同族：错误配置大声失败；
/// 现仓调用方均程序化合法传参，无误拦面）。
/// 大小写不敏感（与 Has 同口径）；空串值由此放行、由各参数既有校验拦截
/// （#55 空串守卫 / #44 非数值守卫），口径不变。</summary>
static bool ArgsAreValid(string[] args, string[] flags, string[] valued)
{
    var seenValued = new List<string>(valued.Length);
    for (var i = 0; i < args.Length; i++)
    {
        var token = args[i];
        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            if (Array.Exists(valued, v => token.Equals(v, StringComparison.OrdinalIgnoreCase)))
            {
                if (seenValued.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    return false;   // 同名带值参数二次出现：后值逃逸校验（如 --mem-mb 0 --mem-mb abc）
                }
                // 带值参数：下一 token 须存在且不以 -- 开头（否则 = 缺值或值为另一 flag 名）
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return false;
                }
                seenValued.Add(token);
                i++;   // 值已归类，跳过
            }
            else if (!Array.Exists(flags, f => token.Equals(f, StringComparison.OrdinalIgnoreCase)))
            {
                return false;   // 未识别参数（拼错参数名）
            }
        }
        else
        {
            return false;   // 裸 token：非任何带值参数的值（值在上分支随 i++ 消费）
        }
    }
    return true;
}

/// <summary>--mem-mb 合法域 [0,1024]；null=提供但缺值/非数值/溢出（issue #44）。唯一校验落点，child/parent 共用。</summary>
static bool IsMemMbInvalid(int? memMb) => memMb is null or < 0 or > 1024;

static int? GetInt(string[] args, string name, int fallback)
{
    var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (index < 0)
    {
        return fallback;    // 未提供参数 → 默认值（合法形态）
    }
    return index + 1 < args.Length && int.TryParse(args[index + 1], out var value)
        ? value
        : null;             // 提供了但缺值/非数值/溢出 → null（调用方按参数错误退出，issue #44）
}

/// <summary>取带值参数值；null = 未提供或提供但缺值（缺值形态已由 ArgsAreValid 前置拦截，此为防御纵深），空串原样返回——调用方守卫按 is not { Length: > 0 } 拦空串（issue #55）。</summary>
static string? GetStr(string[] args, string name)
{
    var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

/// <summary>参数形态集（issue #61 严格校验，与 Usage/README 用法行同步维护）：
/// flags 两模式共用；带值参数按模式互斥（--ready-event 仅 child、--out-pid 仅 parent）。</summary>
internal static class ArgSets
{
    public static readonly string[] Flags = ["--cpu", "--established", "--window", "--ignore-close"];
    public static readonly string[] ChildValued = ["--mem-mb", "--ready-event"];
    public static readonly string[] ParentValued = ["--mem-mb", "--out-pid"];
}

/// <summary>构造资源驻留点：静态引用防 GC 回收（内存驻留/TCP 连接生命周期与进程同寿）。</summary>
internal static class Hold
{
    public static byte[]? Memory;

    private static TcpListener? _listener;
    private static TcpClient? _client;
    private static TcpClient? _accepted;

    public static void StartLoopbackEstablished()
    {
        // 自连回环：同 pid 两端一条 ESTABLISHED（口径 #6 计活跃）
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _client = new TcpClient();
        _client.Connect(IPAddress.Loopback, port);
        _accepted = _listener.AcceptTcpClient();
    }
}

/// <summary>
/// 窗口形态（T-09 R03 释放链路载体）：顶层可见窗口 + 消息循环。
/// 默认 WM_CLOSE → DefWindowProc 销毁窗口 → 进程退出（优雅成功路径）；--ignore-close 吞并 WM_CLOSE
/// （3s 超时转强杀路径）。测试支撑进程，经典 DllImport 薄通道（非产品代码，不入 Core CsWin32 口径）。
/// </summary>
internal static class TestWindow
{
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsVisible = 0x10000000;

    private static bool _ignoreClose;
    private static Native.WndProc? _wndProc;   // 委托保活，防 GC 回收后原生回调失效

    public static void Show(bool ignoreClose)
    {
        _ignoreClose = ignoreClose;
        _wndProc = WndProc;
        var wc = new Native.WNDCLASS
        {
            lpfnWndProc = _wndProc,
            hInstance = Marshal.GetHINSTANCE(typeof(Hold).Module),
            lpszClassName = "MemReliefTestProcsWnd",
        };
        _ = Native.RegisterClassW(ref wc);
        var hwnd = Native.CreateWindowExW(
            0, wc.lpszClassName, "MemRelief.TestProcs", WsOverlappedWindow | WsVisible,
            10, 10, 320, 200, 0, 0, wc.hInstance, 0);
        if (hwnd == 0)
        {
            throw new InvalidOperationException($"CreateWindowExW 失败（Win32 错误 {Marshal.GetLastWin32Error()}）");
        }
    }

    public static void Pump()
    {
        while (Native.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            _ = Native.TranslateMessage(ref msg);
            _ = Native.DispatchMessageW(ref msg);
        }
    }

    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WmClose && _ignoreClose)
        {
            return 0;   // 吞并关闭：模拟对 WM_CLOSE 无响应的应用（3s 超时转强杀）
        }
        if (msg == WmDestroy)
        {
            Native.PostQuitMessage(0);
        }
        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private sealed class Native
    {
        public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASS
        {
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public nint hInstance;
            public nint hIcon;
            public nint hCursor;
            public nint hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public nint hwnd;
            public uint message;
            public nint wParam;
            public nint lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint CreateWindowExW(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height,
            nint parent, nint menu, nint instance, nint param);

        [DllImport("user32.dll")]
        public static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        public static extern nint DispatchMessageW(ref MSG msg);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll")]
        public static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    }
}
