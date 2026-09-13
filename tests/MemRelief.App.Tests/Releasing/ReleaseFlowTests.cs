using MemRelief.App.Releasing;
using MemRelief.App.Scanning;
using MemRelief.App.State;
using MemRelief.App.Tests.TestDoubles;
using MemRelief.App.ViewModels;
using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using MemRelief.Core.Storage;
using Xunit;

namespace MemRelief.App.Tests.Releasing;

/// <summary>
/// T-16 释放交互闭环行为测试：确认弹窗（N 树/X MB、取消停留已展示）、Execute 触发与进度、
/// 结果报告（双释放量/跳过说明/被拉起提示/日志单路径）、已结束项移除、释放中关窗语义。
/// </summary>
public class ReleaseFlowTests
{
    private static readonly DateTime TakenAt = new(2026, 9, 12, 8, 0, 0, DateTimeKind.Utc);

    private static ProcessSnapshot Proc(int pid, string name, long bytes) => new(
        pid, 0, name, $@"C:\apps\{name}", TakenAt.AddMinutes(-1), bytes);

    private static ScanResult Snapshot(params ProcessSnapshot[] procs) => new(
        TakenAt, procs.Length, 10, procs, []);

    private static Classification Classify(int pid, long treeBytes, bool requiresElevation = false, bool revived = false) =>
        new(pid, Level.Recommend, [new Basis(1, "孤儿")], treeBytes,
            revived ? true : null, revived ? [new SourceEntry(SourceType.Service, "svc1")] : [], requiresElevation);

    private static (MainViewModel Vm, FakeReleaser Releaser, FakeConfirmDialog Dialog,
        FakeReleaseLogStore Log, FakeScanner Scanner, FakeRules Rules) NewVm(
        FakeReleaser? releaser = null, bool withReleaser = true)
    {
        releaser ??= new FakeReleaser();
        var scanner = new FakeScanner();
        var rules = new FakeRules();
        var whitelist = new FakeWhitelistStore();
        var dialog = new FakeConfirmDialog();
        var log = new FakeReleaseLogStore();
        var coordinator = new ScanCoordinator(
            scanner, rules, new StaticRulePackStore(),
            new ClassificationContext(1, "u"), () => whitelist.Snapshot());
        var vm = withReleaser
            ? new MainViewModel(new UiStateMachine(), coordinator, rules, whitelist,
                releaser, log, dialog)
            : new MainViewModel(new UiStateMachine(), coordinator, rules, whitelist);
        return (vm, releaser, dialog, log, scanner, rules);
    }

    /// <summary>铺态：扫描成功进入已展示（classify 结果由 rules.Result 预置）。</summary>
    private static async Task ScanAsync(MainViewModel vm) => await vm.StartScanAsync();

    private static void ArrangeReleasing(MainViewModel vm)
    {
        Assert.True(vm.StateMachine.TryTransition(AppTrigger.StartScan));
        Assert.True(vm.StateMachine.TryTransition(AppTrigger.ScanCompleted));
        Assert.True(vm.StateMachine.TryTransition(AppTrigger.ReleaseConfirmed));
        Assert.Equal(AppState.Releasing, vm.StateMachine.State);
    }

    private static IReadOnlyList<ClassificationRow> AllRows(MainViewModel vm) =>
        vm.Groups.SelectMany(g => g.Rows).ToList();

    private static ReleaseReport ReportOf(ReleaseRequest request, params ReleaseItemResult[] items) => new(
        request.ReleaseId, request.RequestedAtUtc, DateTime.UtcNow, DateTime.UtcNow, items,
        MainReleasedBytes: 120 * 1024 * 1024, CheckReleasedBytes: -1 * 1024 * 1024);

    // —— 释放命令准入 ——

    [Fact]
    public async Task 未扫描态_释放编排直接返回_不规划不弹窗()
    {
        var (vm, releaser, dialog, _, _, _) = NewVm();

        await vm.ReleaseAsync();

        Assert.Equal(0, releaser.PlanCalls);
        Assert.Equal(0, dialog.ShowCalls);
        Assert.Equal(AppState.NotScanned, vm.StateMachine.State);
    }

    [Fact]
    public async Task 未注入Releaser_释放编排直接返回_空安全()
    {
        var (vm, _, dialog, _, _, _) = NewVm(withReleaser: false);
        await ScanAsync(vm);

        await vm.ReleaseAsync();

        Assert.Equal(0, dialog.ShowCalls);
    }

    [Fact]
    public async Task 未勾选任何行_不规划不弹窗()
    {
        var (vm, releaser, dialog, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(Snapshot(Proc(100, "a.exe", 60_000_000)));
        rules.Result = [Classify(100, 60_000_000)];
        await ScanAsync(vm);
        AllRows(vm).Single().IsChecked = false;

        await vm.ReleaseAsync();

        Assert.Equal(0, releaser.PlanCalls);
        Assert.Equal(0, dialog.ShowCalls);
    }

    // —— 确认弹窗（N 树/X MB；取消停留已展示态） ——

    [Fact]
    public async Task 弹窗展示_非空树数与合计字节_空计划树不计入N()
    {
        var (vm, releaser, dialog, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(
            Snapshot(Proc(100, "a.exe", 60_000_000), Proc(200, "b.exe", 30_000_000)));
        rules.Result = [Classify(100, 60_000_000), Classify(200, 30_000_000)];
        await ScanAsync(vm);
        releaser.PlanResult =
        [
            new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000),
            new TreePlan(200, [], [], 0), // 勾选根被保护集命中的空计划壳：计入计划但无可执行节点
        ];

        await vm.ReleaseAsync();

        Assert.Equal(1, dialog.ShowCalls);
        Assert.Equal((1, 60_000_000), dialog.LastShown); // N=有节点的树；X=将结束字节合计
    }

    [Fact]
    public async Task 弹窗取消_停留已展示态_无触发_不执行()
    {
        var (vm, releaser, dialog, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(Snapshot(Proc(100, "a.exe", 60_000_000)));
        rules.Result = [Classify(100, 60_000_000)];
        await ScanAsync(vm);
        releaser.PlanResult = [new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000)];
        dialog.Reply = false;

        await vm.ReleaseAsync();

        Assert.Equal(AppState.ResultsShown, vm.StateMachine.State); // 取消不产生状态触发
        Assert.Empty(releaser.ExecutedRequests);
    }

    [Fact]
    public async Task 全部树为空计划_不弹窗不转态()
    {
        var (vm, releaser, dialog, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(Snapshot(Proc(100, "a.exe", 60_000_000)));
        rules.Result = [Classify(100, 60_000_000)];
        await ScanAsync(vm);
        releaser.PlanResult = [new TreePlan(100, [], [], 0)];

        await vm.ReleaseAsync();

        Assert.Equal(0, dialog.ShowCalls);
        Assert.Equal(AppState.ResultsShown, vm.StateMachine.State);
    }

    // —— 确认后执行：请求契约与进度 ——

    [Fact]
    public async Task 确认后_转释放中_Execute收到勾选请求与计划()
    {
        var (vm, releaser, _, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(
            Snapshot(Proc(100, "a.exe", 60_000_000), Proc(200, "b.exe", 30_000_000)));
        rules.Result = [Classify(100, 60_000_000), Classify(200, 30_000_000)];
        await ScanAsync(vm);
        var row200 = AllRows(vm).Single(r => r.Pid == 200);
        row200.IsChecked = false; // 只勾选 100
        releaser.PlanResult = [new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000)];

        await vm.ReleaseAsync();

        Assert.Equal(AppState.ReportShown, vm.StateMachine.State); // 完成事件已收口（假实现同 Core 顺序）
        var request = releaser.ExecutedRequests.Single();
        Assert.Equal(TakenAt, request.SnapshotRef); // 勾选基于哪次扫描（T-08 裁决①）
        Assert.Equal(new HashSet<int> { 100 }, request.SelectedPids);
        Assert.Single(releaser.ExecutedPlans.Single());
    }

    [Fact]
    public async Task 释放中_树终态计数呈现进度()
    {
        var (vm, releaser, _, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(Snapshot(Proc(100, "a.exe", 60_000_000)));
        rules.Result = [Classify(100, 60_000_000)];
        await ScanAsync(vm);
        releaser.PlanResult =
        [
            new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000),
            new TreePlan(200, [new TreeNode(Proc(200, "b.exe", 1))], [], 1),
            new TreePlan(300, [new TreeNode(Proc(300, "c.exe", 1))], [], 1),
        ];
        var gate = new TaskCompletionSource<ReleaseReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        releaser.OnExecute = (_, _) => gate.Task;
        var releaseTask = vm.ReleaseAsync();
        await WaitUntil.ForAsync(() => vm.StateMachine.State == AppState.Releasing); // 编排链异步推进，铺态到位再驱动进度

        releaser.RaiseTreeProgress(100, TreeState.Done);
        releaser.RaiseTreeProgress(200, TreeState.Skipped);
        Assert.Contains("2/3", vm.StatusText); // 终态计数（Done/Skipped/Failed），非终态不计

        releaser.RaiseTreeProgress(200, TreeState.Killing);
        Assert.Contains("2/3", vm.StatusText); // 非终态不改变计数

        gate.SetResult(ReportOf(releaser.ExecutedRequests[0]));
        await releaseTask;
    }

    [Fact]
    public void 释放中_状态文本呈现进行中语义()
    {
        var (vm, _, _, _, _, _) = NewVm();

        ArrangeReleasing(vm);

        Assert.Contains("正在释放", vm.StatusText);
    }

    // —— 结果报告：双释放量/逐项/被拉起提示 ——

    [Fact]
    public async Task 释放完成_报告文案含双释放量与逐项结果与失败标记()
    {
        var (vm, releaser, _, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(
            Snapshot(Proc(100, "a.exe", 60_000_000), Proc(200, "b.exe", 30_000_000)));
        rules.Result = [Classify(100, 60_000_000), Classify(200, 30_000_000, requiresElevation: true)];
        await ScanAsync(vm);
        releaser.PlanResult =
        [
            new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000),
            new TreePlan(200, [new TreeNode(Proc(200, "b.exe", 30_000_000))], [], 30_000_000),
        ];
        releaser.OnExecute = (r, _) => Task.FromResult(ReportOf(r,
            new ReleaseItemResult(100, "a.exe", @"C:\apps\a.exe", null, ReleaseItemOutcome.Released),
            new ReleaseItemResult(200, "b.exe", @"C:\apps\b.exe", null, ReleaseItemOutcome.NeedsElevation, "拒绝访问", 5)));

        await vm.ReleaseAsync();

        var text = vm.ReleaseReportText;
        Assert.Contains("主释放量", text);
        Assert.Contains("120.0 MB", text);
        Assert.Contains("校验释放量", text);
        Assert.Contains("-1.0 MB", text); // 可负如实输出（PRD F3-6）
        Assert.Contains("a.exe (PID 100)：已优雅关闭", text);
        Assert.Contains("b.exe (PID 200)：需管理员权限", text);
        Assert.Contains("可重新扫描", text);
    }

    [Fact]
    public async Task 释放完成_被拉起类结束项附预期提示()
    {
        var (vm, releaser, _, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(Snapshot(Proc(100, "a.exe", 60_000_000)));
        rules.Result = [Classify(100, 60_000_000, revived: true)];
        await ScanAsync(vm);
        releaser.PlanResult = [new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000)];
        releaser.OnExecute = (r, _) => Task.FromResult(ReportOf(r,
            new ReleaseItemResult(100, "a.exe", @"C:\apps\a.exe", null, ReleaseItemOutcome.ForceKilled)));

        await vm.ReleaseAsync();

        var text = vm.ReleaseReportText;
        Assert.Contains("重新拉起", text);
        Assert.Contains("services.msc", text); // #28 裁决②：服务来源分流禁用入口建议
    }

    [Fact]
    public async Task 释放完成_跳过项按原因如实呈现()
    {
        var (vm, releaser, _, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(Snapshot(Proc(100, "a.exe", 60_000_000)));
        rules.Result = [Classify(100, 60_000_000)];
        await ScanAsync(vm);
        releaser.PlanResult = [new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000)];
        releaser.OnExecute = (r, _) => Task.FromResult(ReportOf(r,
            new ReleaseItemResult(100, "svc.exe", null, null, ReleaseItemOutcome.SkippedProtected,
                "保护名单命中")));

        await vm.ReleaseAsync();

        Assert.Contains("svc.exe", vm.ReleaseReportText);
        Assert.Contains("保护名单", vm.ReleaseReportText);
    }

    // —— 已结束项移除 + 可重新扫描 ——

    [Fact]
    public async Task 释放完成_已结束项移除_未结束项保留()
    {
        var (vm, releaser, _, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(
            Snapshot(Proc(100, "a.exe", 60_000_000), Proc(200, "b.exe", 30_000_000)));
        rules.Result = [Classify(100, 60_000_000), Classify(200, 30_000_000, requiresElevation: true)];
        await ScanAsync(vm);
        releaser.PlanResult =
        [
            new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000),
            new TreePlan(200, [new TreeNode(Proc(200, "b.exe", 30_000_000))], [], 30_000_000),
        ];
        releaser.OnExecute = (r, _) => Task.FromResult(ReportOf(r,
            new ReleaseItemResult(100, "a.exe", null, null, ReleaseItemOutcome.Released),
            new ReleaseItemResult(200, "b.exe", null, null, ReleaseItemOutcome.NeedsElevation)));

        await vm.ReleaseAsync();

        var remaining = AllRows(vm).ToList();
        Assert.DoesNotContain(remaining, r => r.Pid == 100); // 已结束项已移除（PRD F3-6）
        Assert.Contains(remaining, r => r.Pid == 200); // 需管理员未结束：保留供提权重启后再处理
    }

    [Fact]
    public async Task 结果展示态_状态文本提示可重新扫描()
    {
        var (vm, releaser, _, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(Snapshot(Proc(100, "a.exe", 60_000_000)));
        rules.Result = [Classify(100, 60_000_000)];
        await ScanAsync(vm);
        releaser.PlanResult = [new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000)];

        await vm.ReleaseAsync();

        Assert.Equal(AppState.ReportShown, vm.StateMachine.State);
        Assert.Contains("重新扫描", vm.StatusText);
    }

    // —— 日志追加编排单路径（AC：ReleaseCompleted 后调 IReleaseLogStore.Append） ——

    [Fact]
    public async Task 释放完成_日志追加单路径_结果回填LogPersisted()
    {
        var (vm, releaser, _, log, scanner, rules) = NewVm();
        await ScanAsync(vm);
        releaser.PlanResult = [new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000)];

        await vm.ReleaseAsync();
        await vm.SettleReleaseAsync(); // 日志追加挪离 UI 线程，收尾等待一并覆盖

        Assert.Equal(1, log.AppendCalls); // 单路径一次
        Assert.True(vm.LastReleaseReport!.LogPersisted);
    }

    [Fact]
    public async Task 释放完成_日志写失败_文案提示未留痕_不阻塞收口()
    {
        var (vm, releaser, _, log, scanner, rules) = NewVm();
        log.NextResult = new ReleaseLogAppendResult(Persisted: false, Error: "磁盘已满");
        await ScanAsync(vm);
        releaser.PlanResult = [new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000)];

        await vm.ReleaseAsync();
        await vm.SettleReleaseAsync();

        Assert.Equal(AppState.ReportShown, vm.StateMachine.State); // 写失败不阻塞释放收口
        Assert.False(vm.LastReleaseReport!.LogPersisted);
        Assert.Contains("未留痕", vm.ReleaseReportText);
    }

    [Fact]
    public async Task 同一报告重复收口_日志不重复追加()
    {
        // 对抗性：编排侧必须保证一次释放至多一次 Append（IReleaseLogStore.Append 非幂等契约）
        var (vm, releaser, _, log, scanner, rules) = NewVm();
        await ScanAsync(vm);
        releaser.PlanResult = [new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000)];
        await vm.ReleaseAsync();
        var report = vm.LastReleaseReport!;

        vm.CompleteRelease(report); // 双通道重复投递（事件+直调）
        await vm.SettleReleaseAsync();

        Assert.Equal(1, log.AppendCalls);
    }

    [Fact]
    public async Task 未注入日志存储_收口不追加_报告LogPersisted为未尝试()
    {
        var (vm, releaser, _, _, scanner, rules) = NewVm();
        var whitelist = new FakeWhitelistStore();
        // 无日志存储的构造路径：释放链照常，仅日志编排不启用
        var coordinator = new ScanCoordinator(
            scanner, rules, new StaticRulePackStore(),
            new ClassificationContext(1, "u"), () => whitelist.Snapshot());
        var vm2 = new MainViewModel(new UiStateMachine(), coordinator, rules, whitelist,
            releaser, null, new FakeConfirmDialog());
        await vm2.StartScanAsync();
        releaser.PlanResult = [new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000)];

        await vm2.ReleaseAsync();

        Assert.Null(vm2.LastReleaseReport!.LogPersisted); // null=未尝试（契约口径）
    }

    // —— 释放中关窗语义（PRD §3.6 注：取消未开始树并等待进行中树收尾） ——

    [Fact]
    public async Task 释放中关窗_转调取消_不允许直接关闭()
    {
        var (vm, releaser, _, _, _, _) = NewVm();
        ArrangeReleasing(vm);

        var canClose = vm.PrepareClose();

        Assert.False(canClose); // 调用方 e.Cancel=true 并等待收尾
        Assert.Equal(1, releaser.CancelCalls); // 取消未开始树（进行中树由 Core 等待收尾）
    }

    [Fact]
    public void 非释放中关窗_允许直接关闭()
    {
        var (vm, releaser, _, _, _, _) = NewVm();

        Assert.True(vm.PrepareClose());
        Assert.Equal(0, releaser.CancelCalls);
    }

    [Fact]
    public async Task 释放中关窗_等待释放收尾后方可关闭()
    {
        var (vm, releaser, dialog, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(Snapshot(Proc(100, "a.exe", 60_000_000)));
        rules.Result = [Classify(100, 60_000_000)];
        await ScanAsync(vm);
        releaser.PlanResult = [new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000)];
        var gate = new TaskCompletionSource<ReleaseReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        releaser.OnExecute = (_, _) => gate.Task;
        var releaseTask = vm.ReleaseAsync();

        Assert.False(vm.PrepareClose()); // 触发取消
        var settle = vm.SettleReleaseAsync();
        await Task.Delay(50);
        Assert.False(settle.IsCompleted); // 进行中树收尾前不得关闭

        gate.SetResult(ReportOf(releaser.ExecutedRequests.Single()));
        await settle;
        await releaseTask;
        Assert.True(settle.IsCompleted); // 收尾完成，调用方此时执行 Close
    }

    // —— 规划失败与执行兜底 ——

    [Fact]
    public async Task 规划失败_提示失败_停留已展示态()
    {
        var (vm, releaser, dialog, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(Snapshot(Proc(100, "a.exe", 60_000_000)));
        rules.Result = [Classify(100, 60_000_000)];
        await ScanAsync(vm);
        releaser.OnPlanError = new InvalidOperationException("保护名单不可用");

        await vm.ReleaseAsync();

        Assert.Equal(AppState.ResultsShown, vm.StateMachine.State); // 停留已展示
        Assert.Equal(0, dialog.ShowCalls);
        Assert.Contains("释放", vm.ReleaseFailedMessage);
    }

    [Fact]
    public async Task 执行意外异常_如实提示_不伪造报告()
    {
        var (vm, releaser, _, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(Snapshot(Proc(100, "a.exe", 60_000_000)));
        rules.Result = [Classify(100, 60_000_000)];
        await ScanAsync(vm);
        releaser.PlanResult = [new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000)];
        releaser.OnExecute = (_, _) => throw new InvalidOperationException("执行链违约");

        await vm.ReleaseAsync(); // 不外抛

        Assert.NotNull(vm.ReleaseFailedMessage);
        Assert.Null(vm.LastReleaseReport); // 无报告不伪造
    }

    [Fact]
    public async Task 执行意外异常_防御出口回已展示态_允许直接关窗()
    {
        // cross-review 收口：状态滞留释放中会致关窗互递归死锁（WPF Close 同步再入 Closing），
        // ReleaseFailed 出口回已展示态（保留旧列表可重试），关窗路径自然放行
        var (vm, releaser, _, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(Snapshot(Proc(100, "a.exe", 60_000_000)));
        rules.Result = [Classify(100, 60_000_000)];
        await ScanAsync(vm);
        releaser.PlanResult = [new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000)];
        releaser.OnExecute = (_, _) => throw new InvalidOperationException("执行链违约");

        await vm.ReleaseAsync();

        Assert.Equal(AppState.ResultsShown, vm.StateMachine.State);
        Assert.True(vm.PrepareClose()); // 非释放中：直接放行，无死锁
    }

    [Fact]
    public async Task 弹窗字节_祖先与后代同勾选_按节点Pid去重计字()
    {
        var (vm, releaser, dialog, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(
            Snapshot(Proc(100, "a.exe", 60_000_000), Proc(200, "b.exe", 30_000_000)));
        rules.Result = [Classify(100, 60_000_000), Classify(200, 30_000_000)];
        await ScanAsync(vm);
        // 祖先树含根100+子200，200 自身亦为勾选根（T-08 裁决⑤各自成树不去重，执行期所有权去重归 Core）
        releaser.PlanResult =
        [
            new TreePlan(100,
            [
                new TreeNode(Proc(100, "a.exe", 60_000_000)),
                new TreeNode(Proc(200, "b.exe", 30_000_000)),
            ], [], 90_000_000),
            new TreePlan(200, [new TreeNode(Proc(200, "b.exe", 30_000_000))], [], 30_000_000),
        ];

        await vm.ReleaseAsync();

        // 200 在两棵树中重复出现，X 只计一次（与 Execute committedByPid 所有权口径同源）：60+30=90MB
        Assert.Equal((2, 90_000_000), dialog.LastShown);
    }

    [Fact]
    public async Task 释放完成_幸存行保留用户勾选与展开态()
    {
        var (vm, releaser, _, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(
            Snapshot(Proc(100, "a.exe", 60_000_000), Proc(200, "b.exe", 30_000_000)));
        rules.Result = [Classify(100, 60_000_000), Classify(200, 30_000_000)];
        await ScanAsync(vm);
        var survivor = AllRows(vm).Single(r => r.Pid == 200);
        survivor.IsChecked = false; // 用户手动取消勾选
        survivor.IsExpanded = true;
        releaser.PlanResult = [new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000)];
        releaser.OnExecute = (r, _) => Task.FromResult(ReportOf(r,
            new ReleaseItemResult(100, "a.exe", null, null, ReleaseItemOutcome.Released)));

        await vm.ReleaseAsync();

        var after = AllRows(vm).Single(r => r.Pid == 200);
        Assert.False(after.IsChecked); // 勾选意图不被重建重置（防下次释放被重新勾上）
        Assert.True(after.IsExpanded);
    }

    [Fact]
    public async Task 取消收尾_报告与状态行呈现已取消而非全部完成()
    {
        var (vm, releaser, _, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(Snapshot(Proc(100, "a.exe", 60_000_000)));
        rules.Result = [Classify(100, 60_000_000)];
        await ScanAsync(vm);
        releaser.PlanResult =
        [
            new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000),
            new TreePlan(200, [new TreeNode(Proc(200, "b.exe", 30_000_000))], [], 30_000_000),
        ];
        var gate = new TaskCompletionSource<ReleaseReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        releaser.OnExecute = (_, _) => gate.Task;
        var releaseTask = vm.ReleaseAsync();
        await WaitUntil.ForAsync(() => vm.StateMachine.State == AppState.Releasing);

        vm.PrepareClose(); // 取消语义（关窗路径与取消按钮同源：RequestCancel）

        gate.SetResult(ReportOf(releaser.ExecutedRequests.Single(),
            new ReleaseItemResult(100, "a.exe", null, null, ReleaseItemOutcome.Released)));
        await releaseTask;

        // R03 GWT“如实反映已执行/已取消”：头行/状态行区分取消收尾，逐项仅记已执行部分（Core 契约）
        Assert.Contains("已取消", vm.ReleaseReportText);
        Assert.Contains("已取消", vm.StatusText);
    }

    [Fact]
    public async Task 双击竞态_被拒调用不清零在途释放进度()
    {
        var (vm, releaser, _, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(Snapshot(Proc(100, "a.exe", 60_000_000)));
        rules.Result = [Classify(100, 60_000_000)];
        await ScanAsync(vm);
        releaser.PlanResult =
        [
            new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000),
            new TreePlan(200, [new TreeNode(Proc(200, "b.exe", 1))], [], 1),
            new TreePlan(300, [new TreeNode(Proc(300, "c.exe", 1))], [], 1),
        ];
        var gate = new TaskCompletionSource<ReleaseReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        releaser.OnExecute = (_, _) => gate.Task;
        var releaseTask = vm.ReleaseAsync();
        await WaitUntil.ForAsync(() => vm.StateMachine.State == AppState.Releasing);

        releaser.RaiseTreeProgress(100, TreeState.Done);
        await vm.ReleaseAsync(); // 第二次调用：状态机拒绝（不执行），亦不得清零在途计数
        releaser.RaiseTreeProgress(200, TreeState.Done);

        Assert.Contains("2/3", vm.StatusText); // 计数连续（重置已收敛到转换成功之后）

        gate.SetResult(ReportOf(releaser.ExecutedRequests[0]));
        await releaseTask;
    }

    // —— 报告关闭 ——

    [Fact]
    public async Task 关闭报告命令_结果展示态回已展示_其他态拒绝()
    {
        var (vm, releaser, _, _, scanner, rules) = NewVm();
        scanner.OnTakeSnapshot = () => Task.FromResult(Snapshot(Proc(100, "a.exe", 60_000_000)));
        rules.Result = [Classify(100, 60_000_000)];
        await ScanAsync(vm);
        releaser.PlanResult = [new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000)];
        await vm.ReleaseAsync();
        Assert.True(vm.CloseReportCommand.CanExecute(null));

        vm.CloseReportCommand.Execute(null);

        Assert.Equal(AppState.ResultsShown, vm.StateMachine.State);
    }
}
