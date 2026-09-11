using MemRelief.Core.Contracts;
using MemRelief.Core.Releaser;
using MemRelief.Core.Tests.Rules;
using Xunit;

namespace MemRelief.Core.Tests.Releaser;

// T-08 释放规划与树保护单测：R03 树保护 GWT（PRD §3.2）+ issue #15 AC#1/#2 契约行为
// 断言口径：TreePlan.Nodes 先序扁平且 Nodes[0]=根；SkippedNodes 原因三值穷尽；
// TreePrivateBytes = 将结束节点私有提交合计（不含跳过子树）
public class TreePlannerTests
{
    private static ScanResult Scan(params ProcessSnapshot[] snapshots) => Snap.Scan(snapshots);

    private static ReleaseRequest Request(ScanResult scan, params int[] pids) =>
        new(Guid.NewGuid(), scan.TakenAtUtc, new HashSet<int>(pids), Snap.T);

    private static IReadOnlyList<TreePlan> Plan(
        ScanResult scan, int[] pids, string[]? whitelist = null, RulePack? pack = null)
    {
        var planner = new TreePlanner();
        return planner.Plan(
            Request(scan, pids),
            scan,
            Snap.Whitelist(whitelist ?? Array.Empty<string>()),
            pack ?? Snap.Pack()).GetAwaiter().GetResult();
    }

    private static int[] NodePids(IReadOnlyList<TreeNode> nodes) =>
        nodes.Select(n => n.Snapshot.Pid).ToArray();

    private static int[] SkipPids(IReadOnlyList<SkippedNode> nodes) =>
        nodes.Select(n => n.Snapshot.Pid).ToArray();

    // —— AC#2 树构建：以勾选项为根 ——

    [Fact]
    public void Plan_勾选根_子树全入计划_先序扁平()
    {
        // 1 → (2 → 4, 3)
        var scan = Scan(
            Snap.Clean(1, ppid: 0),
            Snap.Clean(2, ppid: 1),
            Snap.Clean(4, ppid: 2),
            Snap.Clean(3, ppid: 1));
        var plan = Plan(scan, new[] { 1 }).Single();

        Assert.Equal(1, plan.Id);
        Assert.Equal(1, plan.RootPid);
        Assert.Equal(new[] { 1, 2, 4, 3 }, NodePids(plan.Nodes)); // 先序
        Assert.Equal(new[] { 2, 3 }, NodePids(plan.Nodes[0].Children));
        Assert.Empty(plan.SkippedNodes);
    }

    [Fact]
    public void Plan_单节点树_无子节点()
    {
        var scan = Scan(Snap.Clean(7, ppid: 0));
        var plan = Plan(scan, new[] { 7 }).Single();

        var node = Assert.Single(plan.Nodes);
        Assert.Equal(7, node.Snapshot.Pid);
        Assert.Empty(node.Children);
        Assert.Empty(plan.SkippedNodes);
    }

    [Fact]
    public void Plan_空勾选_返回空计划列表()
    {
        var scan = Scan(Snap.Clean(1));
        Assert.Empty(Plan(scan, Array.Empty<int>()));
    }

    [Fact]
    public void Plan_勾选根不在快照_快速失败()
    {
        var scan = Scan(Snap.Clean(1));
        Assert.Throws<ArgumentException>(() => Plan(scan, new[] { 42 }));
    }

    [Fact]
    public void Plan_多勾选_各出一树_按根升序()
    {
        var scan = Scan(
            Snap.Clean(1, ppid: 0),
            Snap.Clean(2, ppid: 0),
            Snap.Clean(3, ppid: 1));
        var plans = Plan(scan, new[] { 2, 1 });

        Assert.Equal(new[] { 1, 2 }, plans.Select(p => p.RootPid)); // 确定性：根升序
        Assert.Equal(new[] { 1, 3 }, NodePids(plans.Single(p => p.RootPid == 1).Nodes));
        Assert.Equal(new[] { 2 }, NodePids(plans.Single(p => p.RootPid == 2).Nodes));
    }

    [Fact]
    public void Plan_祖先与后代同勾选_重叠各自成树()
    {
        // 1 → 2 → 3：节点 3 同时出现在两计划中（执行期去重归 T-09）
        var scan = Scan(
            Snap.Clean(1, ppid: 0),
            Snap.Clean(2, ppid: 1),
            Snap.Clean(3, ppid: 2));
        var plans = Plan(scan, new[] { 1, 3 });

        var plan1 = plans.Single(p => p.RootPid == 1);
        var plan3 = plans.Single(p => p.RootPid == 3);
        Assert.Equal(new[] { 1, 2, 3 }, NodePids(plan1.Nodes));
        Assert.Equal(new[] { 3 }, NodePids(plan3.Nodes));
    }

    // —— AC#2 树保护集（🚫保护名单 ∪ 白名单）——

    // R03 树保护 GWT（PRD §3.2）：待释放树内含保护名单进程（svchost 子节点）与白名单子进程
    // → 两者及其子树均被跳过，跳过项含原因，其余正常计划
    [Fact]
    public void Gwt_R03树保护_保护与白名单节点及子树跳过_其余正常()
    {
        // 1(40MB) → [2 svchost(10MB) → (3, 4), 5 wapp 白名单(8MB) → 6(2MB), 7(20MB)]
        var scan = Scan(
            Snap.Clean(1, ppid: 0, name: "app.exe", bytes: 40 * Snap.Mb),
            Snap.Clean(2, ppid: 1, name: "svchost.exe", bytes: 10 * Snap.Mb),
            Snap.Clean(3, ppid: 2, name: "a.exe", bytes: 5 * Snap.Mb),
            Snap.Clean(4, ppid: 2, name: "b.exe", bytes: 5 * Snap.Mb),
            Snap.Clean(5, ppid: 1, name: "wapp.exe", bytes: 8 * Snap.Mb),
            Snap.Clean(6, ppid: 5, name: "wchild.exe", bytes: 2 * Snap.Mb),
            Snap.Clean(7, ppid: 1, name: "clean.exe", bytes: 20 * Snap.Mb));
        var plan = Plan(scan, new[] { 1 }, whitelist: new[] { "wapp.exe" }, pack: Snap.Pack()).Single();

        Assert.Equal(new[] { 1, 7 }, NodePids(plan.Nodes));
        Assert.Equal(new[] { 2, 3, 4, 5, 6 }, SkipPids(plan.SkippedNodes));
        Assert.Equal(60 * Snap.Mb, plan.TreePrivateBytes); // 40 + 20，不含跳过的 30MB

        var byPid = plan.SkippedNodes.ToDictionary(n => n.Snapshot.Pid);
        Assert.Equal(TreeSkipReason.ProtectedList, byPid[2].Reason);
        Assert.Contains("保护名单命中：svchost.exe", byPid[2].Detail);
        Assert.Equal(TreeSkipReason.SubtreeOfSkipped, byPid[3].Reason);
        Assert.Contains("Pid 2", byPid[3].Detail); // 连带原因可追溯至保护命中祖先
        Assert.Equal(TreeSkipReason.SubtreeOfSkipped, byPid[4].Reason);
        Assert.Equal(TreeSkipReason.Whitelisted, byPid[5].Reason);
        Assert.Contains("白名单命中：wapp.exe", byPid[5].Detail);
        Assert.Equal(TreeSkipReason.SubtreeOfSkipped, byPid[6].Reason);
    }

    [Fact]
    public void Plan_勾选根自身在保护集_空树计划()
    {
        var scan = Scan(Snap.Clean(2, ppid: 0, name: "svchost.exe", bytes: 10 * Snap.Mb));
        var plan = Plan(scan, new[] { 2 }, pack: Snap.Pack()).Single();

        Assert.Empty(plan.Nodes);
        var skipped = Assert.Single(plan.SkippedNodes);
        Assert.Equal(TreeSkipReason.ProtectedList, skipped.Reason);
        Assert.Equal(0, plan.TreePrivateBytes);
    }

    [Fact]
    public void Plan_白名单匹配_大小写不敏感()
    {
        var scan = Scan(Snap.Clean(1, ppid: 0, name: "myapp.exe"));
        var plan = Plan(scan, new[] { 1 }, whitelist: new[] { "MyApp.EXE" }).Single();

        Assert.Equal(TreeSkipReason.Whitelisted, Assert.Single(plan.SkippedNodes).Reason);
        Assert.Empty(plan.Nodes);
    }

    [Fact]
    public void Plan_双名单同命中_白名单原因优先()
    {
        var scan = Scan(Snap.Clean(1, ppid: 0, name: "svchost.exe"));
        var plan = Plan(scan, new[] { 1 }, whitelist: new[] { "svchost.exe" }, pack: Snap.Pack()).Single();

        Assert.Equal(TreeSkipReason.Whitelisted, Assert.Single(plan.SkippedNodes).Reason);
    }

    [Fact]
    public void Plan_保护子树内自身命中_原因取自身命中()
    {
        // 1(保护) → 2(白名单) → 3：自身命中信息量大于连带，取更具体者
        var scan = Scan(
            Snap.Clean(1, ppid: 0, name: "svchost.exe"),
            Snap.Clean(2, ppid: 1, name: "wapp.exe"),
            Snap.Clean(3, ppid: 2, name: "c.exe"));
        var plan = Plan(scan, new[] { 1 }, whitelist: new[] { "wapp.exe" }, pack: Snap.Pack()).Single();

        var byPid = plan.SkippedNodes.ToDictionary(n => n.Snapshot.Pid);
        Assert.Equal(TreeSkipReason.ProtectedList, byPid[1].Reason);
        Assert.Equal(TreeSkipReason.Whitelisted, byPid[2].Reason);
        Assert.Equal(TreeSkipReason.SubtreeOfSkipped, byPid[3].Reason);
        Assert.Contains("Pid 1", byPid[3].Detail); // 连带引用根因祖先
    }

    [Fact]
    public void Plan_保护名单不可用_快速失败()
    {
        // RulePack.Empty（名单加载失败兜底）→ fail-closed 拒绝规划（裁决⑥，system 法-3 同向）：
        // ⚠️ 项降级期仍可勾选，保护证据为零即全放行，必须在此拦截
        var scan = Scan(Snap.Clean(1, ppid: 0), Snap.Clean(2, ppid: 1));
        Assert.Throws<InvalidOperationException>(() =>
            Plan(scan, new[] { 1 }, pack: RulePack.Empty));
    }

    [Fact]
    public void Plan_SnapshotRef与快照错配_快速失败()
    {
        // 陈旧请求配新快照（PID 复用场景可静默错价"N 树/X MB"），拒绝规划（裁决①）
        var scan = Scan(Snap.Clean(1, ppid: 0));
        var request = new ReleaseRequest(Guid.NewGuid(), scan.TakenAtUtc.AddDays(-1), new HashSet<int> { 1 }, Snap.T);
        var planner = new TreePlanner();
        Assert.Throws<ArgumentException>(() =>
            planner.Plan(request, scan, Snap.Whitelist(), Snap.Pack()).GetAwaiter().GetResult());
    }

    // 保护集口径边界：仅名单（🚫保护名单 ∪ 白名单）承载保护，判定域信号（签名/系统目录等）不在此重算
    // （上游约束：ui 仅允许勾选 ✅/⚠️ 项——PRD R02/F3）
    [Fact]
    public void Plan_保护集仅名单口径_判定信号不参与()
    {
        var scan = Scan(Snap.Clean(1, ppid: 0, signals: new SignalSet(
            HasVisibleWindow: false,
            IsSystemDirectory: true,
            SignatureStatus: SignatureStatus.Microsoft)));
        var plan = Plan(scan, new[] { 1 }).Single();

        Assert.Single(plan.Nodes);
        Assert.Empty(plan.SkippedNodes);
    }

    // —— AC#1 TreePrivateBytes 数据源 ——

    [Fact]
    public void Plan_树合计含深层节点_数值精确()
    {
        // 1(40MB) → 2(30MB) → 3(20MB)
        var scan = Scan(
            Snap.Clean(1, ppid: 0, bytes: 40 * Snap.Mb),
            Snap.Clean(2, ppid: 1, bytes: 30 * Snap.Mb),
            Snap.Clean(3, ppid: 2, bytes: 20 * Snap.Mb));
        var plan = Plan(scan, new[] { 1 }).Single();

        Assert.Equal(90 * Snap.Mb, plan.TreePrivateBytes);
    }

    // —— 对抗性：违约输入与环境边界 ——

    [Fact]
    public void Plan_ppid环违约输入_终止且确定性()
    {
        // 1↔2 互为父（scanner 契约违约）：断回边保终止，树 = {1, 2}
        var scan = Scan(Snap.Clean(1, ppid: 2), Snap.Clean(2, ppid: 1));
        var plan = Plan(scan, new[] { 1 }).Single();

        Assert.Equal(new[] { 1, 2 }, NodePids(plan.Nodes));
        Assert.Empty(plan.SkippedNodes);
    }

    [Fact]
    public void Plan_快照Pid重复违约输入_快速失败()
    {
        // Pid 唯一是 scanner 契约（data-contracts §1.1）：违约输入快速失败（RulesEngine.Query 同款）
        var scan = Scan(Snap.Clean(1, ppid: 0), Snap.Clean(1, ppid: 0));
        Assert.Throws<ArgumentException>(() => Plan(scan, new[] { 1 }));
    }

    [Fact]
    public void Plan_请求集合事后变更_计划不变()
    {
        var scan = Scan(Snap.Clean(1, ppid: 0), Snap.Clean(2, ppid: 0));
        var selected = new HashSet<int> { 1 };
        var request = new ReleaseRequest(Guid.NewGuid(), scan.TakenAtUtc, selected, Snap.T);
        selected.Add(2); // 构造后变更入参集合

        var planner = new TreePlanner();
        var plans = planner.Plan(request, scan, Snap.Whitelist(), Snap.Pack())
            .GetAwaiter().GetResult();

        Assert.Single(plans); // 防御性拷贝：不含事后加入的 2
    }
}
