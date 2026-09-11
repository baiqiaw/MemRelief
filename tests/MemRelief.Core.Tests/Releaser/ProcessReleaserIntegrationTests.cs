using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MemRelief.Core.Contracts;
using MemRelief.Core.Releaser;
using MemRelief.Core.Tests.Rules;
using Xunit;

namespace MemRelief.Core.Tests.Releaser;

// T-09 两段式执行主链真机集成（TestProcs 载体，R03 链路）：身份校验→优雅→3s→强杀、无窗口直杀、
// 多树并行、AC 计时断言（单树 ≤5s 含 3s 优雅等待）。合成侧全分支覆盖见 ProcessReleaserTests。
public class ProcessReleaserIntegrationTests
{
    private static string ExePath() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "MemRelief.TestProcs", "bin", "Debug", "net10.0", "MemRelief.TestProcs.exe"));

    /// <summary>进度事件记录器（Execute 前挂接）。</summary>
    private sealed class ProgressRecorder
    {
        private readonly List<(int Pid, TreeState State)> _events = new();

        public ProgressRecorder Attach(IReleaser releaser)
        {
            releaser.TreeProgress += (pid, state) =>
            {
                lock (_events)
                {
                    _events.Add((pid, state));
                }
            };
            return this;
        }

        public string[] StatesOf(int pid)
        {
            lock (_events)
            {
                return _events.Where(x => x.Pid == pid).Select(x => x.State.ToString()).ToArray();
            }
        }
    }

    // —— 身份探测：与 releaser 同通道（OpenProcess QUERY_LIMITED + GetProcessTimes/QueryFullProcessImageName）——

    private static (DateTime CreationUtc, string Name) ProbeIdentity(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        Assert.NotEqual(nint.Zero, handle);
        try
        {
            Assert.True(GetProcessTimes(handle, out var creation, out _, out _, out _));
            var name = new StringBuilder(1024);
            var size = (uint)name.Capacity;
            Assert.True(QueryFullProcessImageName(handle, 0, name, ref size));
            return (DateTime.FromFileTimeUtc(creation), Path.GetFileName(name.ToString()));
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    private static bool IsAlive(int pid)
    {
        // SYNCHRONIZE|QUERY_LIMITED：WaitForSingleObject(0) 探活（0=已退出，0x102=存活）
        var handle = OpenProcess(Synchronize | QueryLimitedInformation, false, pid);
        if (handle == nint.Zero)
        {
            return false;   // 打不开=已消失（受拒进程在构造进程集中不存在）
        }
        try
        {
            return WaitForSingleObject(handle, 0) == 0x102;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    private static ProcessSnapshot Snapshot(int pid, DateTime creationUtc, string name, int parentPid = 0) =>
        new(pid, parentPid, name, null, creationUtc, 0);

    /// <summary>真实快照 → 完整规划链（IReleaser.Plan，TreePlanner 承载）→ TreePlan。</summary>
    private static IReadOnlyList<TreePlan> PlanLive(ProcessReleaser releaser, params ProcessSnapshot[] snapshots)
    {
        var scan = new ScanResult(DateTime.UtcNow, snapshots.Length, 0, snapshots, Array.Empty<SignalFailure>());
        var request = new ReleaseRequest(Guid.NewGuid(), scan.TakenAtUtc,
            new HashSet<int>(snapshots.Select(s => s.Pid)), DateTime.UtcNow);
        return releaser.Plan(request, scan, Snap.Whitelist(), Snap.Pack()).GetAwaiter().GetResult();
    }

    private static string? _copiedDir;

    /// <summary>
    /// 独立 exe 目录（cross-review 收口）：与 Scanner 侧 TestProcsIntegrationTests 共用同一构造器目录时，
    /// 本类并发存活的孤儿会击穿其"同可执行目录无其他存活进程"断言（xUnit 默认跨类并行）；
    /// 拷贝到独立随机目录隔离，目录留在 %TEMP%（与 pid 文件同口径的测试运行残留）。
    /// </summary>
    private static string CopiedExePath()
    {
        if (_copiedDir is { } dir)
        {
            return Path.Combine(dir, "MemRelief.TestProcs.exe");
        }

        var sourceDir = Path.GetDirectoryName(ExePath())!;
        var exe = ExePath();
        Assert.True(File.Exists(exe), $"构造器未构建：{exe}（gate.ps1 全 sln 构建后应存在）");
        dir = Path.Combine(Path.GetTempPath(), $"mrrel-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        }
        _copiedDir = dir;
        return Path.Combine(dir, "MemRelief.TestProcs.exe");
    }

    /// <summary>构造孤儿测试进程（parent 退出 → child 就绪成孤儿），返回 child pid。</summary>
    private static int SpawnOrphan(params string[] childFlags)
    {
        var exe = CopiedExePath();
        var outPidFile = Path.Combine(Path.GetTempPath(), $"mrrel-{Guid.NewGuid():N}.txt");
        var psi = new ProcessStartInfo(exe) { CreateNoWindow = true, UseShellExecute = false };
        foreach (var arg in new[] { "parent", "--mem-mb", "10", "--out-pid", outPidFile }.Concat(childFlags))
        {
            psi.ArgumentList.Add(arg);
        }
        using var parent = Process.Start(psi);
        Assert.True(parent!.WaitForExit(15_000), "parent 15s 内未退出（child 未就绪或超时）");
        var pid = int.Parse(File.ReadAllLines(outPidFile)[0]);
        File.Delete(outPidFile);
        return pid;
    }

    private static void TryKill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch
        {
            // 已退出/已无权限：清理尽力而为
        }
    }

    /// <summary>
    /// 清理并确认退出：杀进程 + 有界等待死亡确认。并发真机测试共用同目录 TestProcs 形态，
    /// 仅发终止信号不等退出会残留濒死进程，击穿 T-20 构造规格的"同目录无其他存活进程"断言。
    /// </summary>
    private static void Cleanup(int pid)
    {
        TryKill(pid);
        for (var i = 0; i < 60 && IsAlive(pid); i++)
        {
            Thread.Sleep(50);
        }
    }

    private static void AssertGone(int pid, string label)
    {
        // 退出观测窗口 2s：强杀后进程对象回收存在毫秒级延迟
        for (var i = 0; i < 40 && IsAlive(pid); i++)
        {
            Thread.Sleep(50);
        }
        Assert.False(IsAlive(pid), $"{label}（pid {pid}）执行后仍存活");
    }

    // —— 真机：无窗口孤儿 → 直杀路径（AC：无窗口项跳过优雅直接强杀） ——

    [Fact]
    public async Task 真机_无窗口孤儿_直杀路径_快速结束()
    {
        var pid = SpawnOrphan();
        try
        {
            var (creation, name) = ProbeIdentity(pid);
            Assert.Equal("MemRelief.TestProcs.exe", name);
            var releaser = new ProcessReleaser();
            var progress = new ProgressRecorder().Attach(releaser);
            var plans = PlanLive(releaser, Snapshot(pid, creation, name));

            var sw = Stopwatch.StartNew();
            var report = await releaser.Execute(
                new ReleaseRequest(Guid.NewGuid(), DateTime.UtcNow, new HashSet<int> { pid }, DateTime.UtcNow),
                plans);
            sw.Stop();

            var item = Assert.Single(report.Items);
            Assert.Equal(ReleaseItemOutcome.ForceKilled, item.Outcome);
            Assert.True(sw.ElapsedMilliseconds < 2500, $"无窗口直杀不应走优雅等待：{sw.ElapsedMilliseconds}ms");
            AssertGone(pid, "无窗口孤儿");
            Assert.Equal(new[] { "Pending", "Killing", "Done" }, progress.StatesOf(pid));
        }
        finally
        {
            Cleanup(pid);
        }
    }

    // —— 真机：有窗口正常响应 WM_CLOSE → Released（优雅成功路径） ——

    [Fact]
    public async Task 真机_有窗口正常关闭_优雅路径Released()
    {
        var pid = SpawnOrphan("--window");
        try
        {
            var (creation, name) = ProbeIdentity(pid);
            var releaser = new ProcessReleaser();
            var progress = new ProgressRecorder().Attach(releaser);
            var plans = PlanLive(releaser, Snapshot(pid, creation, name));

            var sw = Stopwatch.StartNew();
            var report = await releaser.Execute(
                new ReleaseRequest(Guid.NewGuid(), DateTime.UtcNow, new HashSet<int> { pid }, DateTime.UtcNow),
                plans);
            sw.Stop();

            var item = Assert.Single(report.Items);
            Assert.Equal(ReleaseItemOutcome.Released, item.Outcome);
            Assert.True(sw.ElapsedMilliseconds < 2500, $"正常关闭不应耗尽 3s 等待：{sw.ElapsedMilliseconds}ms");
            AssertGone(pid, "有窗口孤儿");
            Assert.Equal(new[] { "Pending", "Closing", "Waiting", "Done" }, progress.StatesOf(pid));
        }
        finally
        {
            Cleanup(pid);
        }
    }

    // —— 真机：忽略 WM_CLOSE → 3s 超时转强杀（AC：单树 ≤5s，含 3s 优雅等待——计时断言） ——

    [Fact]
    public async Task 真机_忽略关闭_3秒超时转强杀_单树计时在5秒内()
    {
        var pid = SpawnOrphan("--window", "--ignore-close");
        try
        {
            var (creation, name) = ProbeIdentity(pid);
            var releaser = new ProcessReleaser();
            var progress = new ProgressRecorder().Attach(releaser);
            var plans = PlanLive(releaser, Snapshot(pid, creation, name));

            var sw = Stopwatch.StartNew();
            var report = await releaser.Execute(
                new ReleaseRequest(Guid.NewGuid(), DateTime.UtcNow, new HashSet<int> { pid }, DateTime.UtcNow),
                plans);
            sw.Stop();

            var item = Assert.Single(report.Items);
            Assert.Equal(ReleaseItemOutcome.ForceKilled, item.Outcome);
            // AC 计时断言：3s 优雅等待真实发生（≥2.9s）且单树 ≤5s（PRD §3.4）
            Assert.True(sw.ElapsedMilliseconds >= 2900, $"3s 优雅等待未发生：{sw.ElapsedMilliseconds}ms");
            Assert.True(sw.ElapsedMilliseconds <= 5000, $"单树超 5s 预算：{sw.ElapsedMilliseconds}ms");
            AssertGone(pid, "忽略关闭孤儿");
            Assert.Equal(new[] { "Pending", "Closing", "Waiting", "Killing", "Done" }, progress.StatesOf(pid));
        }
        finally
        {
            Cleanup(pid);
        }
    }

    // —— 真机：身份不一致 → IdentityChanged 跳过不执行（AC#1，进程必须仍然存活） ——

    [Fact]
    public async Task 真机_身份不一致_IdentityChanged跳过_进程仍存活()
    {
        var pid = SpawnOrphan();
        try
        {
            var (creation, name) = ProbeIdentity(pid);
            // 快照创建时间偏移 1s（模拟快照后 PID 被复用为新进程）
            var releaser = new ProcessReleaser();
            var plans = PlanLive(releaser, Snapshot(pid, creation.AddSeconds(1), name));

            var report = await releaser.Execute(
                new ReleaseRequest(Guid.NewGuid(), DateTime.UtcNow, new HashSet<int> { pid }, DateTime.UtcNow),
                plans);

            var item = Assert.Single(report.Items);
            Assert.Equal(ReleaseItemOutcome.IdentityChanged, item.Outcome);
            Assert.Contains("身份", item.Reason);
            Assert.True(IsAlive(pid), "身份不一致项不得被执行（防 PID 复用杀错）");
        }
        finally
        {
            Cleanup(pid);
        }
    }

    // —— 真机：执行前进程已退出 → Exited ——

    [Fact]
    public async Task 真机_执行前已退出_Exited()
    {
        var pid = SpawnOrphan();
        try
        {
            var (creation, name) = ProbeIdentity(pid);
            TryKill(pid);
            AssertGone(pid, "预备退出");

            var releaser = new ProcessReleaser();
            var plans = PlanLive(releaser, Snapshot(pid, creation, name));
            var report = await releaser.Execute(
                new ReleaseRequest(Guid.NewGuid(), DateTime.UtcNow, new HashSet<int> { pid }, DateTime.UtcNow),
                plans);

            Assert.Equal(ReleaseItemOutcome.Exited, Assert.Single(report.Items).Outcome);
        }
        finally
        {
            Cleanup(pid);
        }
    }

    // —— 真机：混合树（有窗口忽略关闭 + 无窗口子节点）→ 两路径同树生效，单树 ≤5s ——

    [Fact]
    public async Task 真机_混合树_无窗口子节点与优雅超时父节点_两路径同树生效()
    {
        var windowedPid = SpawnOrphan("--window", "--ignore-close");
        var windowlessPid = SpawnOrphan();
        try
        {
            var (windowedCreation, windowedName) = ProbeIdentity(windowedPid);
            var (windowlessCreation, windowlessName) = ProbeIdentity(windowlessPid);
            var releaser = new ProcessReleaser();
            var plans = PlanLive(releaser,
                Snapshot(windowedPid, windowedCreation, windowedName),
                Snapshot(windowlessPid, windowlessCreation, windowlessName, parentPid: windowedPid));

            var sw = Stopwatch.StartNew();
            var report = await releaser.Execute(
                new ReleaseRequest(Guid.NewGuid(), DateTime.UtcNow,
                    new HashSet<int> { windowedPid, windowlessPid }, DateTime.UtcNow),
                plans);
            sw.Stop();

            Assert.Equal(2, report.Items.Count);
            Assert.Equal(ReleaseItemOutcome.ForceKilled,
                report.Items.Single(i => i.Pid == windowedPid).Outcome);   // 3s 超时转杀
            Assert.Equal(ReleaseItemOutcome.ForceKilled,
                report.Items.Single(i => i.Pid == windowlessPid).Outcome); // 无窗口直杀
            // 单树 ≤5s（PRD §3.4）；下界证明优雅等待真实发生（未整体走直杀捷径）
            Assert.True(sw.ElapsedMilliseconds >= 2900, $"优雅等待未发生：{sw.ElapsedMilliseconds}ms");
            Assert.True(sw.ElapsedMilliseconds <= 5000, $"单树超 5s 预算：{sw.ElapsedMilliseconds}ms");
            AssertGone(windowedPid, "混合树窗口父节点");
            AssertGone(windowlessPid, "混合树无窗口子节点");
        }
        finally
        {
            Cleanup(windowedPid);
            Cleanup(windowlessPid);
        }
    }

    // —— 真机：多树并行 ——

    [Fact]
    public async Task 真机_两孤儿双树并行_各自执行()
    {
        var pid1 = SpawnOrphan();
        var pid2 = SpawnOrphan();
        try
        {
            var (creation1, name1) = ProbeIdentity(pid1);
            var (creation2, name2) = ProbeIdentity(pid2);
            var releaser = new ProcessReleaser();
            var plans = PlanLive(releaser,
                Snapshot(pid1, creation1, name1),
                Snapshot(pid2, creation2, name2));
            Assert.Equal(2, plans.Count); // 双树

            var reports = new List<ReleaseReport>();
            releaser.ReleaseCompleted += r =>
            {
                lock (reports)
                {
                    reports.Add(r);
                }
            };
            var report = await releaser.Execute(
                new ReleaseRequest(Guid.NewGuid(), DateTime.UtcNow,
                    new HashSet<int> { pid1, pid2 }, DateTime.UtcNow),
                plans);

            Assert.Single(reports); // ReleaseCompleted 至多一次
            Assert.Equal(2, report.Items.Count);
            Assert.All(report.Items, i => Assert.Equal(ReleaseItemOutcome.ForceKilled, i.Outcome));
            AssertGone(pid1, "并行孤儿 1");
            AssertGone(pid2, "并行孤儿 2");
        }
        finally
        {
            Cleanup(pid1);
            Cleanup(pid2);
        }
    }

    private const uint QueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inheritHandle, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        nint handle, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(nint handle, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
}
