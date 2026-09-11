using MemRelief.Core.Contracts;
using MemRelief.Core.Releaser;
using MemRelief.Core.Scanner;
using MemRelief.Core.Tests.Rules;
using Xunit;

namespace MemRelief.Core.Tests.Releaser;

// T-10 取消语义 + 结果报告合成单测：
// 取消（PRD F3-5）——未开始树跳过（Pending→Skipped）、进行中树等待收尾、不中断强杀（防半完成态）、
// 重复取消幂等、残留取消不污染下一次执行；ReleaseCompleted 恒至多一次（契约 §1.5）。
// 报告（契约 §1.3 + ③.s4 裁决⑤）——Before=Execute 进入时/After=全部树终态后采样、
// 主释放量=被结束进程快照私有提交合计、校验释放量=commit 前后差（可负如实输出）、采样失败如实空缺。
// 权限二分接线（PRD F3-7）——打开受拒按快照所有者、强杀拒绝按令牌名优先/快照回退。
// 同集合串行：FakeLiveProcess.CallLog 静态共享，防跨类并行互写（ReleaserFakeSerialCollection）。
[Collection("ReleaserFakeSerial")]
public class ProcessReleaserCancelReportTests
{
    public ProcessReleaserCancelReportTests() => FakeLiveProcess.ResetLog();

    // ProgressRecorder 用共享测试辅助（ProgressRecorder.cs），不再类内私有副本

    /// <summary>
    /// 概览采样假实现：gated=true 时首次采样挂在门上（构造"取消落在入口 reset 之后、树分派之前"
    /// 的确定性窗口）；CommitBefore/CommitAfter 驱动校验释放量；Throw 模拟采样失败。
    /// </summary>
    private sealed class FakeOverviewSampler : IScanner
    {
        private readonly TaskCompletionSource<MemoryOverview>? _gate;

        public FakeOverviewSampler(bool gated = false)
        {
            _gate = gated
                ? new TaskCompletionSource<MemoryOverview>(TaskCreationOptions.RunContinuationsAsynchronously)
                : null;
        }

        public long CommitBefore { get; set; } = 1000;
        public long CommitAfter { get; set; } = 400;
        public bool Throw { get; set; }
        public int Calls { get; private set; }

        public Task<MemoryOverview> SampleOverview()
        {
            if (Throw)
            {
                throw new InvalidOperationException("采样注入异常");
            }

            var call = Calls++;
            return call == 0 && _gate is { } gate
                ? gate.Task
                : Task.FromResult(Overview(call == 0 ? CommitBefore : CommitAfter));
        }

        public void ReleaseGate() => _gate?.SetResult(Overview(CommitBefore));

        public static MemoryOverview Overview(long commit) =>
            new(8000, 3000, commit, 16000, null, MemoryOverviewSource.NtQuery);

        public Task<ScanResult> TakeSnapshot() => throw new NotSupportedException();

        public Task<ScanResult> CollectSignatures(ScanResult snapshot, ISet<int> candidatePids) =>
            throw new NotSupportedException();
    }

    private static ProcessReleaser CreateReleaser(
        FakeProcessOpener opener, FakeOverviewSampler sampler, out ProgressRecorder progress)
    {
        var releaser = new ProcessReleaser(new TreePlanner(), opener, sampler)
        {
            GraceWaitMs = 200,
            CurrentUserName = "tester",
        };
        progress = new ProgressRecorder();
        progress.Attach(releaser);
        return releaser;
    }

    private static ReleaseRequest Request(IReadOnlySet<int>? pids = null) =>
        new(Guid.NewGuid(), Snap.T, pids ?? new HashSet<int>(), Snap.T);

    private static TreeNode Node(int pid, long? bytes = null, string? owner = null) =>
        new(Snap.Clean(pid, bytes: bytes ?? 60 * Snap.Mb) with { OwnerUser = owner });

    private static async Task<ReleaseReport> ExecuteOnce(
        ProcessReleaser releaser, ReleaseRequest request, IReadOnlyList<TreePlan> plans)
    {
        var reports = new List<ReleaseReport>();
        releaser.ReleaseCompleted += r => reports.Add(r);
        var report = await releaser.Execute(request, plans);
        Assert.Single(reports); // ReleaseCompleted 至多一次（契约 §1.5）
        return report;
    }

    // —— 取消：未开始树跳过（取消窗口 = 入口 reset 之后、树分派之前，经采样门确定性构造） ——

    [Fact]
    public async Task 取消_未开始树全部跳过_Pending转Skipped_无逐项结果()
    {
        var opener = new FakeProcessOpener();
        opener.AddLive(1);
        opener.AddLive(2);
        var sampler = new FakeOverviewSampler(gated: true);
        var releaser = CreateReleaser(opener, sampler, out var progress);
        var plans = new[]
        {
            new TreePlan(1, new[] { Node(1) }, Array.Empty<SkippedNode>(), 0),
            new TreePlan(2, new[] { Node(2) }, Array.Empty<SkippedNode>(), 0),
        };

        var reports = new List<ReleaseReport>();
        releaser.ReleaseCompleted += r => reports.Add(r);
        var executeTask = releaser.Execute(Request(), plans);
        releaser.Cancel(); // 入口 reset 已发生、树尚未分派：两树全部"未开始"
        releaser.Cancel(); // 重复取消幂等
        sampler.ReleaseGate();
        var report = await executeTask;
        Assert.Single(reports); // ReleaseCompleted 至多一次（契约 §1.5）

        Assert.Empty(report.Items); // 报告记已执行部分（PRD F3-6），取消树不产出逐项结果
        Assert.Equal(new[] { "Pending", "Skipped" }, progress.StatesOf(1));
        Assert.Equal(new[] { "Pending", "Skipped" }, progress.StatesOf(2));
        Assert.DoesNotContain("open:1", FakeLiveProcess.CallLog); // 未开始树未触达任何进程
        Assert.DoesNotContain("terminate:2", FakeLiveProcess.CallLog);
        Assert.Equal(0, report.MainReleasedBytes);
        // 取消收尾仍是完整报告：Before/After 均采样（③.s4 裁决⑤"含取消收尾"）
        Assert.NotNull(report.Before);
        Assert.NotNull(report.After);
        Assert.Equal(600, report.CheckReleasedBytes);
        Assert.Equal(2, sampler.Calls);
    }

    [Fact]
    public async Task 残留取消不污染下一次执行_入口重置()
    {
        var opener = new FakeProcessOpener();
        opener.AddLive(1);
        var sampler = new FakeOverviewSampler(gated: true);
        var releaser = CreateReleaser(opener, sampler, out _);
        var plans = new[] { new TreePlan(1, new[] { Node(1) }, Array.Empty<SkippedNode>(), 0) };

        // 第一次释放：取消收尾（顺序语义——上一次释放完成后才发起下一次，PRD §3.6 防重入口径）
        var first = releaser.Execute(Request(), plans);
        releaser.Cancel();
        releaser.Cancel();
        sampler.ReleaseGate();
        var cancelledReport = await first;
        Assert.Empty(cancelledReport.Items);

        var second = await ExecuteOnce(releaser, Request(), plans); // 新一次释放：残留取消不作数
        Assert.Equal(ReleaseItemOutcome.ForceKilled, second.Items.Single().Outcome);
    }

    // —— 取消：进行中树等待收尾，优雅等待不被打断 ——

    [Fact]
    public async Task 取消落在优雅等待段_树继续收尾_Released不被打断()
    {
        var opener = new FakeProcessOpener();
        var live = opener.AddLive(1, windows: new nint[] { 1001 });
        live.ExitOnProbe = 2; // 第二次探活退出
        var sampler = new FakeOverviewSampler();
        var releaser = CreateReleaser(opener, sampler, out var progress);
        var cancelFired = false;
        live.OnWait = () =>
        {
            if (!cancelFired)
            {
                cancelFired = true;
                releaser.Cancel(); // 取消落在等待段内（确定性时点）
            }
        };

        var plan = new TreePlan(1, new[] { Node(1) }, Array.Empty<SkippedNode>(), 0);
        var report = await ExecuteOnce(releaser, Request(), new[] { plan });

        var item = report.Items.Single();
        Assert.Equal(ReleaseItemOutcome.Released, item.Outcome); // 已开始的树等待收尾，不被取消截断
        Assert.DoesNotContain("terminate:1", FakeLiveProcess.CallLog);
        Assert.Equal(new[] { "Pending", "Closing", "Waiting", "Done" }, progress.StatesOf(1));
        Assert.Equal(60 * Snap.Mb, report.MainReleasedBytes);
    }

    // —— 取消：不中断强杀（防半完成态） ——

    [Fact]
    public async Task 取消落在强杀段_强杀照常完成_ForceKilled终态()
    {
        var opener = new FakeProcessOpener();
        var live = opener.AddLive(1); // 无窗口 → 直杀路径
        var sampler = new FakeOverviewSampler();
        var releaser = CreateReleaser(opener, sampler, out var progress);
        live.OnTerminate = () => releaser.Cancel(); // 取消落在强杀段内（确定性时点）
        var plans = new[] { new TreePlan(1, new[] { Node(1) }, Array.Empty<SkippedNode>(), 0) };

        var report = await ExecuteOnce(releaser, Request(), plans);

        var item = report.Items.Single();
        Assert.Equal(ReleaseItemOutcome.ForceKilled, item.Outcome); // 强杀不因取消中断，无半完成态
        Assert.Equal(1, live.TerminateCalls);
        Assert.Equal(new[] { "Pending", "Killing", "Done" }, progress.StatesOf(1));
        Assert.Equal(60 * Snap.Mb, report.MainReleasedBytes);
    }

    // —— 双释放量（PRD F3-6：主释放量=被结束进程快照私有提交合计；校验释放量=commit 前后差） ——

    [Fact]
    public async Task 报告_双释放量主口径_被结束项合计_commit差为正()
    {
        var opener = new FakeProcessOpener();
        opener.AddLive(1);
        opener.AddLive(2);
        var sampler = new FakeOverviewSampler { CommitBefore = 1000, CommitAfter = 400 };
        var releaser = CreateReleaser(opener, sampler, out _);
        var plan = new TreePlan(1,
            new[] { Node(1, 60 * Snap.Mb), Node(2, 30 * Snap.Mb) }, Array.Empty<SkippedNode>(), 0);

        var report = await ExecuteOnce(releaser, Request(), new[] { plan });

        Assert.All(report.Items, i => Assert.Equal(ReleaseItemOutcome.ForceKilled, i.Outcome));
        Assert.Equal(90 * Snap.Mb, report.MainReleasedBytes); // 主释放量=被结束进程快照私有提交合计
        Assert.NotNull(report.Before);
        Assert.NotNull(report.After);
        Assert.Equal(600, report.CheckReleasedBytes); // commit 前后差为正=系统提交量下降
    }

    [Fact]
    public async Task 报告_校验释放量为负_如实输出()
    {
        var opener = new FakeProcessOpener();
        opener.AddLive(1);
        var sampler = new FakeOverviewSampler { CommitBefore = 400, CommitAfter = 900 };
        var releaser = CreateReleaser(opener, sampler, out _);
        var plan = new TreePlan(1, new[] { Node(1) }, Array.Empty<SkippedNode>(), 0);

        var report = await ExecuteOnce(releaser, Request(), new[] { plan });

        Assert.Equal(-500, report.CheckReleasedBytes); // 负值（如其他进程增长）不截断、如实输出
        Assert.Equal(60 * Snap.Mb, report.MainReleasedBytes); // 主释放量不受系统噪声影响
    }

    [Fact]
    public async Task 报告_采样失败_BeforeAfterCheck如实空缺_主释放量照算()
    {
        var opener = new FakeProcessOpener();
        opener.AddLive(1);
        opener.AddLive(2);
        var sampler = new FakeOverviewSampler { Throw = true };
        var releaser = CreateReleaser(opener, sampler, out _);
        var plan = new TreePlan(1,
            new[] { Node(1, 60 * Snap.Mb), Node(2, 30 * Snap.Mb) }, Array.Empty<SkippedNode>(), 0);

        var report = await ExecuteOnce(releaser, Request(), new[] { plan });

        Assert.Null(report.Before);
        Assert.Null(report.After);
        Assert.Null(report.CheckReleasedBytes); // 任一时点缺失 → 校验值不伪造
        Assert.Equal(90 * Snap.Mb, report.MainReleasedBytes); // 主释放量不依赖采样
    }

    [Fact]
    public async Task 报告_主释放量仅计被结束项_Exited与IdentityChanged不计入()
    {
        var opener = new FakeProcessOpener();
        opener.AddLive(1);                                   // ForceKilled：计入
        opener.AddVanished(2);                               // Exited：非被结束，不计入
        opener.AddLive(3, identityMatch: false);             // IdentityChanged：未执行，不计入
        var sampler = new FakeOverviewSampler();
        var releaser = CreateReleaser(opener, sampler, out _);
        var plan = new TreePlan(1,
            new[] { Node(1, 60 * Snap.Mb), Node(2, 30 * Snap.Mb), Node(3, 15 * Snap.Mb) },
            Array.Empty<SkippedNode>(), 0);

        var report = await ExecuteOnce(releaser, Request(), new[] { plan });

        Assert.Equal(ReleaseItemOutcome.ForceKilled, Item(report, 1).Outcome);
        Assert.Equal(ReleaseItemOutcome.Exited, Item(report, 2).Outcome);
        Assert.Equal(ReleaseItemOutcome.IdentityChanged, Item(report, 3).Outcome);
        Assert.Equal(60 * Snap.Mb, report.MainReleasedBytes);
    }

    [Fact]
    public async Task 报告_空计划_采样与双释放量兜底_完成事件照发()
    {
        var opener = new FakeProcessOpener();
        var sampler = new FakeOverviewSampler { CommitBefore = 800, CommitAfter = 500 };
        var releaser = CreateReleaser(opener, sampler, out _);

        var report = await ExecuteOnce(releaser, Request(), Array.Empty<TreePlan>());

        Assert.Empty(report.Items);
        Assert.Equal(0, report.MainReleasedBytes);
        Assert.Equal(300, report.CheckReleasedBytes);
        Assert.Equal(2, sampler.Calls);
    }

    // —— 权限二分接线（PRD F3-7）：打开受拒按快照所有者（无句柄，令牌不可得） ——

    [Fact]
    public async Task 打开受拒_所有者非当前用户_NeedsElevation携带错误码()
    {
        var opener = new FakeProcessOpener();
        opener.AddDenied(3, 5);
        var sampler = new FakeOverviewSampler();
        var releaser = CreateReleaser(opener, sampler, out _);
        var plan = new TreePlan(3, new[] { Node(3, owner: "other") }, Array.Empty<SkippedNode>(), 0);

        var report = await ExecuteOnce(releaser, Request(), new[] { plan });

        var item = Item(report, 3);
        Assert.Equal(ReleaseItemOutcome.NeedsElevation, item.Outcome);
        Assert.Equal(5, item.ErrorCode);
        Assert.Contains("需管理员", item.Reason);
    }

    [Fact]
    public async Task 打开受拒_所有者为当前用户_Blocked()
    {
        var opener = new FakeProcessOpener();
        opener.AddDenied(3, 5);
        var sampler = new FakeOverviewSampler();
        var releaser = CreateReleaser(opener, sampler, out _);
        var plan = new TreePlan(3, new[] { Node(3, owner: "tester") }, Array.Empty<SkippedNode>(), 0);

        var report = await ExecuteOnce(releaser, Request(), new[] { plan });

        var item = Item(report, 3);
        Assert.Equal(ReleaseItemOutcome.Blocked, item.Outcome);
        Assert.Equal(5, item.ErrorCode);
    }

    // —— 权限二分接线：强杀拒绝按令牌名优先、快照所有者回退；非拒绝错误不二分 ——

    [Fact]
    public async Task 强杀拒绝_令牌名优先于快照_NeedsElevation()
    {
        var opener = new FakeProcessOpener();
        var live = opener.AddLive(1, terminateError: 5);
        live.TokenUserName = "other"; // 执行期令牌：他用户（服务/PPL 常态）
        var sampler = new FakeOverviewSampler();
        var releaser = CreateReleaser(opener, sampler, out _);
        var plan = new TreePlan(1, new[] { Node(1, owner: "tester") }, Array.Empty<SkippedNode>(), 0);

        var report = await ExecuteOnce(releaser, Request(), new[] { plan });

        var item = Item(report, 1);
        Assert.Equal(ReleaseItemOutcome.NeedsElevation, item.Outcome); // 令牌名胜于快照所有者
        Assert.Equal(5, item.ErrorCode);
        Assert.True(live.WaitExitProbeCalls >= 1); // 仍存活复核后二分（非等待期竞态误判）
    }

    [Fact]
    public async Task 强杀拒绝_令牌不可读_回退快照所有者_Blocked()
    {
        var opener = new FakeProcessOpener();
        var live = opener.AddLive(1, terminateError: 5);
        live.TokenUserName = null; // 令牌不可读
        var sampler = new FakeOverviewSampler();
        var releaser = CreateReleaser(opener, sampler, out _);
        var plan = new TreePlan(1, new[] { Node(1, owner: "tester") }, Array.Empty<SkippedNode>(), 0);

        var report = await ExecuteOnce(releaser, Request(), new[] { plan });

        Assert.Equal(ReleaseItemOutcome.Blocked, Item(report, 1).Outcome); // 当前用户仍失败→被拦截
    }

    [Fact]
    public async Task 强杀失败_非拒绝访问错误_机械Blocked不二分()
    {
        var opener = new FakeProcessOpener();
        var live = opener.AddLive(1, terminateError: 6);
        live.TokenUserName = "other";
        var sampler = new FakeOverviewSampler();
        var releaser = CreateReleaser(opener, sampler, out _);
        var plan = new TreePlan(1, new[] { Node(1, owner: "tester") }, Array.Empty<SkippedNode>(), 0);

        var report = await ExecuteOnce(releaser, Request(), new[] { plan });

        var item = Item(report, 1);
        Assert.Equal(ReleaseItemOutcome.Blocked, item.Outcome); // 二分仅适用于 Access Denied（PRD F3-7）
        Assert.Equal(6, item.ErrorCode);
    }

    private static ReleaseItemResult Item(ReleaseReport report, int pid) =>
        report.Items.Single(i => i.Pid == pid);
}
