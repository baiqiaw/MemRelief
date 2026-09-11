using MemRelief.Core.Contracts;

namespace MemRelief.Core.Releaser;

/// <summary>
/// 释放树规划器（releaser 模块，T-08）：树构建唯一承载——确认弹窗（T-16 消费"N 树/X MB"）
/// 与执行（T-09 Execute）复用同一 TreePlan，树结构不在此之外二次构建。
/// 约束（data-contracts §2 T-08 裁决⑥⑦）：纯函数——同输入必得同输出，零 I/O 零可变状态；
/// 树保护集 = 🚫保护名单 ∪ 白名单，命中节点及其子树跳过并记录原因（PRD F3-3）；
/// 保护名单不可用（空 ProtectedProcesses）→ 拒绝规划（fail-closed，system 法-3 同向）。
/// </summary>
public sealed class TreePlanner
{
    /// <summary>
    /// 以勾选项为根逐个构建进程树并应用保护集标记。
    /// 快速失败（违约输入 / 不可用状态，不静默放行）：
    /// 勾选根不在快照（勾选须来自本次扫描快照）、SnapshotRef 与 scan.TakenAtUtc 错配、
    /// 保护名单为空（名单不可用，拒绝规划）→ ArgumentException / InvalidOperationException。
    /// 计划序 = 根 Pid 升序（确定性输出）。
    /// </summary>
    public Task<IReadOnlyList<TreePlan>> Plan(
        ReleaseRequest request,
        ScanResult scan,
        WhitelistSnapshot whitelist,
        RulePack rulePack)
    {
        IReadOnlyList<TreePlan> result = PlanCore(request, scan, whitelist, rulePack);
        return Task.FromResult(result);
    }

    private static IReadOnlyList<TreePlan> PlanCore(
        ReleaseRequest request,
        ScanResult scan,
        WhitelistSnapshot whitelist,
        RulePack rulePack)
    {
        if (request.SnapshotRef != scan.TakenAtUtc)
        {
            throw new ArgumentException(
                $"SnapshotRef（{request.SnapshotRef:O}）与快照 TakenAtUtc（{scan.TakenAtUtc:O}）错配：" +
                "勾选须基于本次扫描快照，陈旧请求拒绝规划", nameof(request));
        }

        if (rulePack.ProtectedProcesses.Count == 0)
        {
            // 名单不可用（含加载失败经编排方传入的 RulePack.Empty）：⚠️ 项降级期仍可勾选，
            // 保护证据为零即全放行 = fail-open，拒绝规划（裁决⑥，system 法-3 同向）
            throw new InvalidOperationException(
                "保护名单不可用（ProtectedProcesses 为空），拒绝规划（fail-closed，裁决⑥）");
        }

        var byPid = scan.Snapshots.ToDictionary(p => p.Pid); // Pid 唯一是 scanner 契约（data-contracts §1.1），违约输入快速失败
        var children = scan.Snapshots
            .GroupBy(p => p.ParentPid)
            .ToDictionary(g => g.Key, g => g.ToList());

        var plans = new List<TreePlan>();
        foreach (var rootPid in request.SelectedPids.OrderBy(pid => pid))
        {
            if (!byPid.TryGetValue(rootPid, out var root))
            {
                throw new ArgumentException(
                    $"勾选根 Pid {rootPid} 不在快照中（勾选须来自本次扫描快照）", nameof(request));
            }

            var nodes = new List<TreeNode>();
            var skipped = new List<SkippedNode>();
            var built = Build(root, children, whitelist, rulePack,
                skippedAncestor: null, skipped, new HashSet<int>());
            if (built != null)
            {
                FlattenPreOrder(built, nodes);
            }

            plans.Add(new TreePlan(
                rootPid,
                rootPid,
                nodes,
                skipped,
                nodes.Sum(n => n.Snapshot.PrivateCommittedBytes)));
        }

        return plans;
    }

    /// <summary>
    /// 递归构建子树：保护集命中（自身白名单/保护名单，或处于被跳过子树内）→ 记入 skipped
    /// 并继续下探登记连带项，返回 null（不进计划树）；否则返回节点（Children 仅含未跳过分支）。
    /// 原因优先级：自身白名单 > 自身保护名单 > 子树连带——白名单最高优先与 rules 判定同款；
    /// 自身命中信息量大于连带，取更具体者。连带 Detail 引用根因祖先=最外层名单命中节点（裁决④）。
    /// visited 断回边：真实快照 PPID 不成环（工程先验），环属违约输入，防御性保证终止
    /// （RulesEngine.ComputeTreePrivateBytes 同款口径）。
    /// </summary>
    private static TreeNode? Build(
        ProcessSnapshot node,
        Dictionary<int, List<ProcessSnapshot>> children,
        WhitelistSnapshot whitelist,
        RulePack rulePack,
        SkippedAncestor? skippedAncestor,
        List<SkippedNode> skipped,
        HashSet<int> visited)
    {
        if (!visited.Add(node.Pid))
        {
            return null;
        }

        TreeSkipReason? reason = null;
        string detail = string.Empty; // 仅 reason 非 null 时被读取（编译器明确赋值分析不追踪该关联）
        if (whitelist.ContainsName(node.Name))
        {
            reason = TreeSkipReason.Whitelisted;
            detail = $"白名单命中：{node.Name}";
        }
        else if (rulePack.ContainsProtectedProcess(node.Name))
        {
            reason = TreeSkipReason.ProtectedList;
            detail = $"保护名单命中：{node.Name}";
        }
        else if (skippedAncestor != null)
        {
            reason = TreeSkipReason.SubtreeOfSkipped;
            detail = $"子树跳过：祖先 Pid {skippedAncestor.Value.Pid}（{skippedAncestor.Value.Name}）{skippedAncestor.Value.Cause}";
        }

        if (reason != null)
        {
            RegisterSkippedSubtree(node, children, whitelist, rulePack,
                reason.Value, detail, skippedAncestor, skipped, visited);
            return null;
        }

        var childNodes = new List<TreeNode>();
        foreach (var kid in ChildrenOf(node, children))
        {
            var built = Build(kid, children, whitelist, rulePack, null, skipped, visited);
            if (built != null)
            {
                childNodes.Add(built);
            }
        }

        return new TreeNode(node, childNodes);
    }

    /// <summary>登记被跳过节点并下探整棵子树登记连带项（本节点不进计划树，恒返回后由调用方落 null）。</summary>
    private static void RegisterSkippedSubtree(
        ProcessSnapshot node,
        Dictionary<int, List<ProcessSnapshot>> children,
        WhitelistSnapshot whitelist,
        RulePack rulePack,
        TreeSkipReason reason,
        string detail,
        SkippedAncestor? skippedAncestor,
        List<SkippedNode> skipped,
        HashSet<int> visited)
    {
        skipped.Add(new SkippedNode(node, reason, detail));
        // 根因引用贯穿整棵被跳过子树：连带节点一律引用最初的名单命中祖先。
        // 此分支构造根因时 reason 必为自身名单命中（有上级根因则沿用，不重构造）
        var selfAncestor = skippedAncestor
            ?? new SkippedAncestor(
                node.Pid,
                node.Name,
                reason == TreeSkipReason.Whitelisted ? "白名单命中" : "保护名单命中");
        foreach (var kid in ChildrenOf(node, children))
        {
            Build(kid, children, whitelist, rulePack, selfAncestor, skipped, visited);
        }
    }

    private static IReadOnlyList<ProcessSnapshot> ChildrenOf(
        ProcessSnapshot node,
        Dictionary<int, List<ProcessSnapshot>> children) =>
        children.TryGetValue(node.Pid, out var kids) ? kids : Array.Empty<ProcessSnapshot>();

    private static void FlattenPreOrder(TreeNode node, List<TreeNode> into)
    {
        into.Add(node);
        foreach (var child in node.Children)
        {
            FlattenPreOrder(child, into);
        }
    }

    /// <summary>被跳过子树的根因祖先（名单命中节点），连带 Detail 引用它。</summary>
    private readonly record struct SkippedAncestor(int Pid, string Name, string Cause);
}
