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

        退出码：0 成功；2 就绪超时；1 参数错误（WinExe 无控制台，错误详情不可见，用法见 README）。
        """);
    return 1;
}

static int RunChild(string[] args)
{
    var memMb = GetInt(args, "--mem-mb", 60);
    var spinCpu = Has(args, "--cpu");
    var established = Has(args, "--established");
    var readyEvent = GetStr(args, "--ready-event");
    var windowed = Has(args, "--window");
    var ignoreClose = Has(args, "--ignore-close");

    // 私有提交 >50MB：托管数组存活即保持 commit charge（口径 #13 PrivateUsage）
    if (memMb is < 1 or > 1024)
    {
        return 1;   // 参数越界（WinExe 无控制台，退出码 1=参数错误；用法见 README）
    }

    if (memMb > 0)
    {
        Hold.Memory = new byte[memMb * 1024L * 1024L];
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

    var readyName = $"MemRelief.TestProcs.Ready.{Guid.NewGuid():N}";
    using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);

    var child = StartChild(exe, memMb, spinCpu, established, windowed, ignoreClose, readyName);

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

static int GetInt(string[] args, string name, int fallback)
{
    var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value) ? value : fallback;
}

static string? GetStr(string[] args, string name)
{
    var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
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
