using MemRelief.Core.Contracts;
using MemRelief.Core.Releaser;
using MemRelief.Core.Tests.Rules;
using Xunit;

namespace MemRelief.Core.Tests.Releaser;

// T-09 两段式执行主链合成单测：身份校验/优雅→3s→强杀/无窗口直杀/去重/逐项结果映射/进度事件序列。
// 全部分支以 FakeProcessOpener 驱动（确定性、无真实进程）；真机路径与计时 AC 见 ProcessReleaserIntegrationTests。
public class ProcessReleaserTests
{
    public ProcessReleaserTests() => FakeLiveProcess.ResetLog();

    /// <summary>进度事件记录器（Execute 前挂接，事后按树读取状态序）。</summary>
    private sealed class ProgressRecorder
    {
        private readonly List<(int Pid, TreeState State)> _events = new();

        public void Attach(ProcessReleaser releaser) =>
            releaser.TreeProgress += (pid, state) =>
            {
                lock (_events)
                {
                    _events.Add((pid, state));
                }
            };

        public string[] StatesOf(int pid)
        {
            lock (_events)
            {
                return _events.Where(x => x.Pid == pid).Select(x => x.State.ToString()).ToArray();
            }
        }

        public int TotalCount
        {
            get
            {
                lock (_events)
                {
                    return _events.Count;
                }
            }
        }
    }

    private static ProcessReleaser CreateReleaser(FakeProcessOpener opener, out ProgressRecorder progress)
    {
        // 合成时钟：GraceWaitMs 压缩（生产恒 3000，真机计时断言归集成测试）
        var releaser = new ProcessReleaser(new TreePlanner(), opener) { GraceWaitMs = 200 };
        progress = new ProgressRecorder();
        progress.Attach(releaser);
        return releaser;
    }

    private static ReleaseRequest Request(IReadOnlySet<int>? pids = null) =>
        new(Guid.NewGuid(), Snap.T, pids ?? new HashSet<int>(), Snap.T);

    private static TreeNode Node(int pid, params TreeNode[] children) =>
        new(Snap.Clean(pid, ppid: children.Length > 0 ? pid - 1 : 0), children);

    private static ReleaseItemResult Item(IReadOnlyList<ReleaseItemResult> items, int pid) =>
        items.Single(i => i.Pid == pid);

    private static ReleaseReport Execute(ProcessReleaser releaser, IReadOnlyList<TreePlan> plans)
    {
        var reports = new List<ReleaseReport>();
        releaser.ReleaseCompleted += r => reports.Add(r);
        var report = releaser.Execute(Request(), plans).GetAwaiter().GetResult();
        Assert.Single(reports); // ReleaseCompleted 至多一次（契约 §1.5）
        return report;
    }

    // —— AC#1 身份校验：不一致 → IdentityChanged 跳过不执行 ——

    [Fact]
    public void Execute_身份不一致_IdentityChanged跳过_不投递关闭不终止()
    {
        var opener = new FakeProcessOpener();
        var live = opener.AddLive(1, identityMatch: false);
        var releaser = CreateReleaser(opener, out _);

        var plan = new TreePlan(1, new[] { Node(1) }, Array.Empty<SkippedNode>(), 0);
        var report = Execute(releaser, new[] { plan });

        var item = Item(report.Items, 1);
        Assert.Equal(ReleaseItemOutcome.IdentityChanged, item.Outcome);
        Assert.Equal(0, live.ClosePostedTo.Count);
        Assert.Equal(0, live.TerminateCalls);
        Assert.True(live.Disposed); // 句柄即时回收，防泄漏
        // 防杀错：不一致项不进入任何结束通道
        Assert.DoesNotContain("terminate:1", FakeLiveProcess.CallLog);
    }

    [Fact]
    public void Execute_身份不可判_同IdentityChanged兜底()
    {
        // 快照创建时间哨兵/存活侧读取失败 → IdentityMatches=false（Win32LiveProcess 判定，覆盖率豁免层），
        // 合成层以 IdentityMatch=false 表达同语义出口；此处断言该出口映射 IdentityChanged
        var opener = new FakeProcessOpener();
        opener.AddLive(7, identityMatch: false);
        var releaser = CreateReleaser(opener, out _);

        var plan = new TreePlan(7, new[] { Node(7) }, Array.Empty<SkippedNode>(), 0);
        var report = Execute(releaser, new[] { plan });

        Assert.Equal(ReleaseItemOutcome.IdentityChanged, Item(report.Items, 7).Outcome);
    }

    // —— 打开结局分类 ——

    [Fact]
    public void Execute_打开受拒_Blocked携带错误码()
    {
        var opener = new FakeProcessOpener();
        opener.AddDenied(3, 5);
        var releaser = CreateReleaser(opener, out _);

        var plan = new TreePlan(3, new[] { Node(3) }, Array.Empty<SkippedNode>(), 0);
        var report = Execute(releaser, new[] { plan });

        var item = Item(report.Items, 3);
        Assert.Equal(ReleaseItemOutcome.Blocked, item.Outcome); // 机械临时分类，权限二分归 T-10
        Assert.Equal(5, item.ErrorCode);
    }

    [Fact]
    public void Execute_执行时进程已消失_Exited()
    {
        var opener = new FakeProcessOpener();
        opener.AddVanished(4);
        var releaser = CreateReleaser(opener, out _);

        var plan = new TreePlan(4, new[] { Node(4) }, Array.Empty<SkippedNode>(), 0);
        var report = Execute(releaser, new[] { plan });

        Assert.Equal(ReleaseItemOutcome.Exited, Item(report.Items, 4).Outcome);
    }

    // —— AC#3 无窗口直杀路径 ——

    [Fact]
    public void Execute_整树无窗口_直达强杀_不走优雅等待()
    {
        var opener = new FakeProcessOpener();
        opener.AddLive(1);
        var releaser = CreateReleaser(opener, out var progress);

        var plan = new TreePlan(1, new[] { Node(1) }, Array.Empty<SkippedNode>(), 0);
        var report = Execute(releaser, new[] { plan });

        Assert.Equal(ReleaseItemOutcome.ForceKilled, Item(report.Items, 1).Outcome);
        // 直达边：无窗口项不投递关闭、不做优雅探活等待
        Assert.DoesNotContain("close:1", FakeLiveProcess.CallLog);
        Assert.DoesNotContain("wait:1", FakeLiveProcess.CallLog);
        Assert.Equal(new[] { "open:1", "identity:1", "windows:1", "terminate:1", "dispose:1" }, FakeLiveProcess.CallLog);
        Assert.Equal(new[] { "Pending", "Killing", "Done" }, progress.StatesOf(1));
    }

    [Fact]
    public void Execute_混合树_无窗口项随优雅投递同段立即强杀()
    {
        // 1（有窗口，第 3 次探活才退）+ 2（无窗口）：2 的终止发生在 Closing 段（先于任何 wait 探活）
        var opener = new FakeProcessOpener();
        var windowed = opener.AddLive(1, windows: new nint[] { 1001 });
        windowed.ExitOnProbe = 3;
        opener.AddLive(2);
        var releaser = CreateReleaser(opener, out _);

        var plan = new TreePlan(1, new[] { Node(1), Node(2) }, Array.Empty<SkippedNode>(), 0);
        var report = Execute(releaser, new[] { plan });

        Assert.Equal(ReleaseItemOutcome.Released, Item(report.Items, 1).Outcome);
        Assert.Equal(ReleaseItemOutcome.ForceKilled, Item(report.Items, 2).Outcome);
        // 时序：无窗口项先于有窗口项的优雅探活被终结（Closing 段直杀，不陪等 3s）
        var terminate2 = FakeLiveProcess.CallLog.IndexOf("terminate:2");
        var wait1 = FakeLiveProcess.CallLog.IndexOf("wait:1");
        Assert.True(terminate2 >= 0 && wait1 > terminate2, $"时序异常：{string.Join(",", FakeLiveProcess.CallLog)}");
    }

    // —— 优雅路径：WM_CLOSE → 3s → 强杀 ——

    [Fact]
    public void Execute_有窗口正常关闭_Waiting内退出_Released不转杀()
    {
        var opener = new FakeProcessOpener();
        var live = opener.AddLive(1, windows: new nint[] { 1001, 1002 });
        live.ExitOnProbe = 2; // 第二次探活返回已退出
        var releaser = CreateReleaser(opener, out var progress);

        var plan = new TreePlan(1, new[] { Node(1) }, Array.Empty<SkippedNode>(), 0);
        var report = Execute(releaser, new[] { plan });

        Assert.Equal(ReleaseItemOutcome.Released, Item(report.Items, 1).Outcome);
        Assert.Equal(new nint[] { 1001, 1002 }, live.ClosePostedTo); // 全部顶层可见窗口均投递
        Assert.Equal(0, live.TerminateCalls); // 优雅成功不转杀
        // 状态序列：Pending → Closing → Waiting → Done（无幸存者不进 Killing）
        Assert.Equal(new[] { "Pending", "Closing", "Waiting", "Done" }, progress.StatesOf(1));
    }

    [Fact]
    public void Execute_忽略关闭_3s超时转强杀_ForceKilled()
    {
        var opener = new FakeProcessOpener();
        opener.AddLive(1, windows: new nint[] { 1001 });
        var releaser = CreateReleaser(opener, out var progress);

        var plan = new TreePlan(1, new[] { Node(1) }, Array.Empty<SkippedNode>(), 0);
        var report = Execute(releaser, new[] { plan });

        Assert.Equal(ReleaseItemOutcome.ForceKilled, Item(report.Items, 1).Outcome);
        Assert.Contains("close:1", FakeLiveProcess.CallLog);
        Assert.Equal(new[] { "Pending", "Closing", "Waiting", "Killing", "Done" }, progress.StatesOf(1));
    }

    [Fact]
    public void Execute_优雅等待受预算约束_不超时无限轮询()
    {
        // 永不退出的窗口项：探活轮询必须随预算截止（合成 200ms），Execute 快速返回（结构 ≤5s 的机器侧表达）
        var opener = new FakeProcessOpener();
        opener.AddLive(1, windows: new nint[] { 1001 });
        var releaser = CreateReleaser(opener, out _);

        var plan = new TreePlan(1, new[] { Node(1) }, Array.Empty<SkippedNode>(), 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Execute(releaser, new[] { plan });
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000, $"等待预算未生效：{sw.ElapsedMilliseconds}ms");
    }

    // —— 强杀失败复核 ——

    [Fact]
    public void Execute_强杀失败仍存活_Blocked携带错误码()
    {
        var opener = new FakeProcessOpener();
        opener.AddLive(1, terminateError: 5);
        var releaser = CreateReleaser(opener, out _);

        var plan = new TreePlan(1, new[] { Node(1) }, Array.Empty<SkippedNode>(), 0);
        var report = Execute(releaser, new[] { plan });

        var item = Item(report.Items, 1);
        Assert.Equal(ReleaseItemOutcome.Blocked, item.Outcome);
        Assert.Equal(5, item.ErrorCode);
    }

    [Fact]
    public void Execute_强杀失败但已退_如实归Exited()
    {
        var opener = new FakeProcessOpener();
        var live = opener.AddLive(1);
        live.TerminateError = 6;
        live.ExitOnProbe = 1; // 终止失败后的存活复核探活即返回已退出（等待末尾竞态）
        var releaser = CreateReleaser(opener, out _);

        var plan = new TreePlan(1, new[] { Node(1) }, Array.Empty<SkippedNode>(), 0);
        var report = Execute(releaser, new[] { plan });

        Assert.Equal(ReleaseItemOutcome.Exited, Item(report.Items, 1).Outcome);
    }

    // —— 裁决②：执行期去重 ——

    [Fact]
    public void Execute_重叠节点双树_仅属主执行_单结果()
    {
        // 1 → 2：两计划均含 pid 2 → 仅计划 1（传入序）属主执行，pid 2 恰一终止调用
        var opener = new FakeProcessOpener();
        opener.AddLive(1);
        opener.AddLive(2);
        var releaser = CreateReleaser(opener, out var progress);

        var snapshot2 = Snap.Clean(2, ppid: 1);
        var plan1 = new TreePlan(1, new[] { Node(1), new TreeNode(snapshot2) }, Array.Empty<SkippedNode>(), 0);
        var plan2 = new TreePlan(2, new[] { new TreeNode(snapshot2) }, Array.Empty<SkippedNode>(), 0);
        var report = Execute(releaser, new[] { plan1, plan2 });

        Assert.Equal(2, report.Items.Count);
        Assert.Single(FakeLiveProcess.CallLog.Where(x => x == "terminate:2")); // 恰一次终止
        Assert.Equal(ReleaseItemOutcome.ForceKilled, Item(report.Items, 2).Outcome);
        // 壳树（节点全被去重）无实际动作 → Skipped
        Assert.Equal(new[] { "Pending", "Killing", "Done" }, progress.StatesOf(1));
        Assert.Equal(new[] { "Pending", "Skipped" }, progress.StatesOf(2));
    }

    [Fact]
    public void Execute_祖先树连带跳过_自身树节点_执行胜出()
    {
        // W(白名单) → X：树 W 中 X 连带跳过，树 X 勾选自身 → X 照常执行，结果唯一且非跳过
        var opener = new FakeProcessOpener();
        opener.AddLive(9); // X
        var releaser = CreateReleaser(opener, out _);

        var w = Snap.Clean(5, ppid: 0, name: "wapp.exe");
        var x = Snap.Clean(9, ppid: 5);
        var planW = new TreePlan(5,
            Array.Empty<TreeNode>(),
            new[]
            {
                new SkippedNode(w, TreeSkipReason.Whitelisted, "白名单命中：wapp.exe", TreeSkipReason.Whitelisted),
                new SkippedNode(x, TreeSkipReason.SubtreeOfSkipped, "子树跳过", TreeSkipReason.Whitelisted),
            },
            0);
        var planX = new TreePlan(9, new[] { new TreeNode(x) }, Array.Empty<SkippedNode>(), 0);
        var report = Execute(releaser, new[] { planW, planX });

        Assert.Equal(2, report.Items.Count);
        Assert.Equal(ReleaseItemOutcome.SkippedWhitelisted, Item(report.Items, 5).Outcome);
        Assert.Equal(ReleaseItemOutcome.ForceKilled, Item(report.Items, 9).Outcome); // 执行胜出于连带跳过
        Assert.Single(report.Items.Where(i => i.Pid == 9)); // 单 pid 恰一项
    }

    [Fact]
    public void Execute_跳过项双树共现_单跳过结果()
    {
        var opener = new FakeProcessOpener();
        var releaser = CreateReleaser(opener, out _);

        var w = Snap.Clean(5, ppid: 0, name: "wapp.exe");
        var plan1 = new TreePlan(1, new[] { Node(1) },
            new[] { new SkippedNode(w, TreeSkipReason.Whitelisted, "白名单命中", TreeSkipReason.Whitelisted) }, 0);
        var plan2 = new TreePlan(2, new[] { Node(2) },
            new[] { new SkippedNode(w, TreeSkipReason.Whitelisted, "白名单命中", TreeSkipReason.Whitelisted) }, 0);
        var report = Execute(releaser, new[] { plan1, plan2 });

        Assert.Equal(3, report.Items.Count);
        Assert.Single(report.Items.Where(i => i.Pid == 5));
        Assert.Equal(ReleaseItemOutcome.SkippedWhitelisted, Item(report.Items, 5).Outcome);
    }

    [Fact]
    public void Execute_空计划保护根_树Skipped_保护跳过项()
    {
        var opener = new FakeProcessOpener();
        var releaser = CreateReleaser(opener, out var progress);

        var svchost = Snap.Clean(2, ppid: 0, name: "svchost.exe");
        var plan = new TreePlan(2, Array.Empty<TreeNode>(),
            new[]
            {
                new SkippedNode(svchost, TreeSkipReason.ProtectedList, "保护名单命中：svchost.exe",
                    TreeSkipReason.ProtectedList),
            },
            0);
        var report = Execute(releaser, new[] { plan });

        Assert.Equal(new[] { "Pending", "Skipped" }, progress.StatesOf(2));
        var item = Assert.Single(report.Items);
        Assert.Equal(ReleaseItemOutcome.SkippedProtected, item.Outcome);
        Assert.Equal(2, item.Pid);
    }

    [Fact]
    public void Execute_连带跳过按根因映射名单类()
    {
        var opener = new FakeProcessOpener();
        var releaser = CreateReleaser(opener, out _);

        // 连带根因=保护名单 → SkippedProtected；连带根因=白名单 → SkippedWhitelisted
        var child1 = Snap.Clean(10, ppid: 2);
        var child2 = Snap.Clean(20, ppid: 5);
        var plan = new TreePlan(1, Array.Empty<TreeNode>(),
            new[]
            {
                new SkippedNode(child1, TreeSkipReason.SubtreeOfSkipped, "子树跳过", TreeSkipReason.ProtectedList),
                new SkippedNode(child2, TreeSkipReason.SubtreeOfSkipped, "子树跳过", TreeSkipReason.Whitelisted),
            }, 0);
        var report = Execute(releaser, new[] { plan });

        Assert.Equal(ReleaseItemOutcome.SkippedProtected, Item(report.Items, 10).Outcome);
        Assert.Equal(ReleaseItemOutcome.SkippedWhitelisted, Item(report.Items, 20).Outcome);
    }

    // —— 报告与事件 ——

    [Fact]
    public void Execute_报告字段_按契约填充且Items按Pid升序()
    {
        var opener = new FakeProcessOpener();
        opener.AddLive(30);
        opener.AddLive(10);
        var releaser = CreateReleaser(opener, out _);

        var request = new ReleaseRequest(Guid.NewGuid(), Snap.T, new HashSet<int> { 10, 30 }, Snap.T);
        var plans = new List<TreePlan>
        {
            new(30, new[] { Node(30) }, Array.Empty<SkippedNode>(), 0),
            new(10, new[] { Node(10) }, Array.Empty<SkippedNode>(), 0),
        };

        ReleaseReport? received = null;
        releaser.ReleaseCompleted += r => received = r;
        var report = releaser.Execute(request, plans).GetAwaiter().GetResult();

        Assert.Same(report, received); // 至多一次且即返回实例
        Assert.Equal(request.ReleaseId, report.ReleaseId);
        Assert.Equal(request.RequestedAtUtc, report.RequestedAtUtc);
        Assert.True(report.StartedAtUtc <= report.FinishedAtUtc);
        Assert.Null(report.Before); // 采样归 T-10
        Assert.Null(report.After);
        Assert.Null(report.MainReleasedBytes); // 双释放量归 T-10
        Assert.Null(report.CheckReleasedBytes);
        Assert.Null(report.LogPersisted); // App 编排回填
        Assert.Equal(new[] { 10, 30 }, report.Items.Select(i => i.Pid).ToArray()); // 确定性排序
        // 计划未被变更（Execute 复用不重建不改写）
        Assert.Equal(30, plans[0].RootPid);
        Assert.Single(plans[0].Nodes);
    }

    [Fact]
    public void Execute_空计划列表_空报告完成事件仍至多一次()
    {
        var opener = new FakeProcessOpener();
        var releaser = CreateReleaser(opener, out _);

        var report = Execute(releaser, Array.Empty<TreePlan>());

        Assert.Empty(report.Items);
        Assert.True(report.StartedAtUtc <= report.FinishedAtUtc);
    }

    [Fact]
    public async Task Execute_空参数_快速失败()
    {
        var releaser = CreateReleaser(new FakeProcessOpener(), out _);
        await Assert.ThrowsAsync<ArgumentNullException>(() => releaser.Execute(null!, Array.Empty<TreePlan>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => releaser.Execute(Request(), null!));
    }

    [Fact]
    public void Execute_逐树进度按状态机顺序外发()
    {
        // 全流程树（优雅+转杀）与直杀树并行：各自状态序正确、树间不串号
        var opener = new FakeProcessOpener();
        opener.AddLive(1, windows: new nint[] { 1001 }); // 忽略关闭 → 全流程
        opener.AddLive(2);                                // 无窗口 → 直杀
        var releaser = CreateReleaser(opener, out var progress);

        var plans = new[]
        {
            new TreePlan(1, new[] { Node(1) }, Array.Empty<SkippedNode>(), 0),
            new TreePlan(2, new[] { Node(2) }, Array.Empty<SkippedNode>(), 0),
        };
        Execute(releaser, plans);

        Assert.Equal(
            new[] { "Pending", "Closing", "Waiting", "Killing", "Done" },
            progress.StatesOf(1));
        Assert.Equal(
            new[] { "Pending", "Killing", "Done" },
            progress.StatesOf(2));
        Assert.Equal(8, progress.TotalCount); // 5 + 3，树级粒度无额外事件
    }

    // —— 防护分支反向验证（cross-review 收口：每条兜底分支须有测试真实触发）——

    [Fact]
    public void Execute_窗口自查失败_优雅兜底_不投递仍等待转杀()
    {
        // 裁决⑦兜底：自查失败（Windows=null）→ 按有窗口走优雅路径——不投递（无句柄）、仍等待+转杀
        var opener = new FakeProcessOpener();
        var live = opener.AddLive(1);
        live.Windows = null;
        var releaser = CreateReleaser(opener, out var progress);

        var plan = new TreePlan(1, new[] { Node(1) }, Array.Empty<SkippedNode>(), 0);
        var report = Execute(releaser, new[] { plan });

        Assert.Equal(ReleaseItemOutcome.ForceKilled, Item(report.Items, 1).Outcome);
        Assert.DoesNotContain("close:1", FakeLiveProcess.CallLog); // 无句柄可投
        Assert.Contains("wait:1", FakeLiveProcess.CallLog);        // 优雅等待真实发生
        Assert.Equal(new[] { "Pending", "Closing", "Waiting", "Killing", "Done" }, progress.StatesOf(1));
    }

    [Fact]
    public void Execute_意外异常_逐pid兜底不丢项_Failed终态()
    {
        // 防御 catch：在途节点逐 pid 出兜底项（§1.3"每 Pid 恰一项"）+ 句柄统一回收 + Failed 终态
        var opener = new FakeProcessOpener();
        var healthy = opener.AddLive(1);
        var throwing = opener.AddLive(2);
        throwing.ThrowOnWindows = true;
        var releaser = CreateReleaser(opener, out var progress);

        var plan = new TreePlan(1, new[] { Node(1), Node(2) }, Array.Empty<SkippedNode>(), 0);
        var report = Execute(releaser, new[] { plan });

        Assert.Equal(2, report.Items.Count);
        Assert.All(report.Items, i => Assert.Equal(ReleaseItemOutcome.Blocked, i.Outcome));
        Assert.Equal(new[] { 1, 2 }, report.Items.Select(i => i.Pid).ToArray());
        Assert.True(healthy.Disposed);
        Assert.True(throwing.Disposed);
        Assert.Equal(new[] { "Pending", "Failed" }, progress.StatesOf(1));
    }

    [Fact]
    public void Execute_事件订阅者异常_不击穿执行与报告()
    {
        // 通知通道 misuse 就地隔离：进度/完成订阅者抛异常，执行与返回的报告不受影响。
        // （多播语义：异常订阅者会使同事件后续订阅者本轮不触发，故此处不再叠加收集订阅者）
        var opener = new FakeProcessOpener();
        opener.AddLive(1);
        var releaser = CreateReleaser(opener, out _);
        releaser.TreeProgress += (_, _) => throw new InvalidOperationException("订阅者注入异常");
        releaser.ReleaseCompleted += _ => throw new InvalidOperationException("订阅者注入异常");

        var plan = new TreePlan(1, new[] { Node(1) }, Array.Empty<SkippedNode>(), 0);
        var report = releaser.Execute(Request(), new[] { plan }).GetAwaiter().GetResult();

        Assert.Equal(ReleaseItemOutcome.ForceKilled, Item(report.Items, 1).Outcome);
        Assert.True(report.FinishedAtUtc >= report.StartedAtUtc);
    }

    [Fact]
    public void Execute_违约输入_重复根Pid或树内重复节点_快速失败()
    {
        var opener = new FakeProcessOpener();
        var releaser = CreateReleaser(opener, out _);

        // 同一计划实例出现两次（根 Pid 重复）
        var plan = new TreePlan(1, new[] { Node(1) }, Array.Empty<SkippedNode>(), 0);
        Assert.Throws<ArgumentException>(() =>
            releaser.Execute(Request(), new[] { plan, plan }).GetAwaiter().GetResult());

        // 单计划树内重复节点 pid
        var snapshot = Snap.Clean(7);
        var planDup = new TreePlan(1,
            new[] { new TreeNode(snapshot), new TreeNode(snapshot) }, Array.Empty<SkippedNode>(), 0);
        Assert.Throws<ArgumentException>(() =>
            releaser.Execute(Request(), new[] { planDup }).GetAwaiter().GetResult());
    }

    [Fact]
    public void Plan_委托TreePlanner_产出计划复用不重建()
    {
        // IReleaser.Plan 与 TreePlanner 同一承载（裁决①）：同一输入同计划
        var scan = Snap.Scan(Snap.Clean(1, ppid: 0), Snap.Clean(2, ppid: 1));
        var request = new ReleaseRequest(Guid.NewGuid(), scan.TakenAtUtc, new HashSet<int> { 1 }, Snap.T);
        var releaser = CreateReleaser(new FakeProcessOpener(), out _);

        var plans = releaser.Plan(request, scan, Snap.Whitelist(), Snap.Pack()).GetAwaiter().GetResult();

        var plan = Assert.Single(plans);
        Assert.Equal(1, plan.RootPid);
        Assert.Equal(new[] { 1, 2 }, plan.Nodes.Select(n => n.Snapshot.Pid).ToArray());
    }
}
