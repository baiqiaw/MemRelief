namespace MemRelief.Core.Contracts;

// 释放域契约（提供者 releaser；ui、storage 消费）。定义见 docs/specs/interfaces/data-contracts.md §1.3。
// T-08 交付规划侧（ReleaseRequest/TreePlan）；Execute 侧 ReleaseItemResult/ReleaseReport 归 T-09/T-10 契约化。

/// <summary>释放请求（编排方构造）。SnapshotRef = 目标快照标识（= ScanResult.TakenAtUtc），声明勾选基于哪次扫描。</summary>
public record ReleaseRequest(
    Guid ReleaseId,
    DateTime SnapshotRef,
    IReadOnlySet<int> SelectedPids,
    DateTime RequestedAtUtc)
{
    public IReadOnlySet<int> SelectedPids { get; init; } =
        SelectedPids == null ? new HashSet<int>() : new HashSet<int>(SelectedPids);
}

/// <summary>树内跳过原因。保护集 = 🚫保护名单 ∪ 白名单（PRD F3-3）；子树连带单列，区分自身命中与连带跳过。</summary>
public enum TreeSkipReason
{
    /// <summary>🚫保护名单（RulePack.ProtectedProcesses 穷举名，OrdinalIgnoreCase）命中。</summary>
    ProtectedList,

    /// <summary>白名单命中（复用 WhitelistSnapshot.ContainsName——全系统唯一白名单匹配点）。</summary>
    Whitelisted,

    /// <summary>无自身名单命中，因祖先被跳过而连带（"其子进程将于下次扫描重新评估"）。</summary>
    SubtreeOfSkipped,
}

/// <summary>树内节点：快照引用 + 子节点。仅承载将结束节点（被跳过分支在 TreePlan.SkippedNodes）。</summary>
public record TreeNode(ProcessSnapshot Snapshot, IReadOnlyList<TreeNode>? Children = null)
{
    public IReadOnlyList<TreeNode> Children { get; init; } =
        Children == null ? Array.Empty<TreeNode>() : Children.ToArray();
}

/// <summary>跳过项：节点 + 原因。Detail 人读（SignalFailure 同款口径，不作判定输入）。</summary>
public record SkippedNode(ProcessSnapshot Snapshot, TreeSkipReason Reason, string Detail);

/// <summary>
/// 单树释放计划（确认弹窗"N 树/X MB"数据源，T-09 Execute 复用）。
/// Id = RootPid（契约）；Nodes = 树内将结束节点，先序扁平（Nodes[0] = 根），层级经 TreeNode.Children 唯一承载；
/// SkippedNodes = 保护集跳过项（节点+原因）；
/// TreePrivateBytes = 将结束节点私有提交合计（PRD F3-1"将结束…合计约 X MB"预估，不含跳过子树；
/// 与判定域 Classification.TreePrivateBytes[口径 #13 全后代合计，R02 树占用展示] 语义分立，
/// 无保护命中时两值相等——data-contracts §2 T-08 裁决③）。
/// </summary>
public record TreePlan(
    int Id,
    int RootPid,
    IReadOnlyList<TreeNode> Nodes,
    IReadOnlyList<SkippedNode> SkippedNodes,
    long TreePrivateBytes)
{
    public IReadOnlyList<TreeNode> Nodes { get; init; } =
        Nodes == null ? Array.Empty<TreeNode>() : Nodes.ToArray();
    public IReadOnlyList<SkippedNode> SkippedNodes { get; init; } =
        SkippedNodes == null ? Array.Empty<SkippedNode>() : SkippedNodes.ToArray();
}
