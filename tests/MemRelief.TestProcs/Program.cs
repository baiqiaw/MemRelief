using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

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
          parent [--mem-mb N] [--cpu] [--established] [--out-pid <file>]
              启动 child → 等 child 就绪 → 本进程退出（child 成为孤儿）。
              --out-pid：首行=child pid。
          child  [--mem-mb N] [--cpu] [--established]
              挂起等待被外部终止；--mem-mb 0 = 仅挂起（同目录存活场景由活父直接启动本形态）。

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

    if (readyEvent is { } name)
    {
        using var evt = EventWaitHandle.OpenExisting(name);
        evt.Set();   // 就绪信号：内存/连接已就位，父可退出造孤儿
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
    var outPidPath = GetStr(args, "--out-pid");

    var readyName = $"MemRelief.TestProcs.Ready.{Guid.NewGuid():N}";
    using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);

    var child = StartChild(exe, memMb, spinCpu, established, readyName);

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

static Process StartChild(string exe, int memMb, bool spinCpu, bool established, string? readyName)
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
