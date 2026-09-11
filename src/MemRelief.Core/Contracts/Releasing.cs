namespace MemRelief.Core.Contracts;

// 释放域契约（提供者 releaser；ui、storage 消费）。定义见 docs/specs/interfaces/data-contracts.md §1.3。
// T-08 交付规划侧（ReleaseRequest/TreePlan）；T-09 交付执行侧契约化（TreeState/ReleaseItemOutcome/
// ReleaseItemResult/ReleaseReport）；Cancel 与报告采样/释放量字段填充归 T-10（data-contracts §2 T-09 裁决）。

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

/// <summary>
/// 跳过项：节点 + 原因。Detail 人读（SignalFailure 同款口径，不作判定输入）。
/// RootCause = 跳过根因的名单类（自身命中 = Reason；连带 = 名单命中祖先自身原因，恒不为 SubtreeOfSkipped）——
/// 释放报告逐项结果映射 SkippedProtected/SkippedWhitelisted 的依据（data-contracts §2 T-09 裁决）。
/// </summary>
public record SkippedNode(ProcessSnapshot Snapshot, TreeSkipReason Reason, string Detail, TreeSkipReason RootCause);

/// <summary>
/// 单树释放计划（确认弹窗"N 树/X MB"数据源，T-09 Execute 复用）。
/// RootPid = 根进程 Pid，计划列表内唯一（勾选根互异），即树级进度事件（TreeProgress）的树标识
/// （T-09 裁决：原 Id 字段与 RootPid 恒等冗余，收敛为单字段）；
/// Nodes = 树内将结束节点，先序扁平（Nodes[0] = 根），层级经 TreeNode.Children 唯一承载；
/// SkippedNodes = 保护集跳过项（节点+原因）；
/// TreePrivateBytes = 将结束节点私有提交合计（PRD F3-1"将结束…合计约 X MB"预估，不含跳过子树；
/// 与判定域 Classification.TreePrivateBytes[口径 #13 全后代合计，R02 树占用展示] 语义分立，
/// 无保护命中时两值相等——data-contracts §2 T-08 裁决③）。
/// </summary>
public record TreePlan(
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

/// <summary>
/// 单树执行技术状态（releaser.md §4.2 状态机，T-09 契约化；经 TreeProgress 逐树外发，树级粒度）。
/// 主链：Pending → Closing → Waiting(3s) → Killing → Done；无可见窗口直达边 Pending → Killing；
/// 优雅期全部退出直达边 Waiting → Done（无幸存者不进 Killing）；
/// 全程无将结束节点（空计划/去重壳树/校验层全出局/取消跳过[随 T-10]）→ Pending → Skipped；
/// Failed 为防御性终态（T-09 仅在意料外异常兜底路径可达，正常失败均映射到逐项 Outcome）。
/// </summary>
public enum TreeState
{
    Pending,
    Closing,
    Waiting,
    Killing,
    Done,
    Skipped,
    Failed,
}

/// <summary>
/// 释放逐项结果 8 值分类（契约 §1.3；NeedsElevation 与 Blocked 的权限二分归 T-10，
/// T-09 对结束失败先按机械事实出 Blocked+错误码，T-10 按所有者/令牌二分细化）。
/// </summary>
public enum ReleaseItemOutcome
{
    /// <summary>优雅关闭成功（WM_CLOSE 后退出，含关闭与退出间窗口极小的自退）。</summary>
    Released,

    /// <summary>强制结束（TerminateProcess 成功；无窗口项直杀与优雅超时转杀同值）。</summary>
    ForceKilled,

    /// <summary>已自行退出（执行到达时进程已不存在）。</summary>
    Exited,

    /// <summary>进程已变化跳过（进程名+创建时间与快照不一致或不可判——防 PID 复用杀错，fail-closed 不执行）。</summary>
    IdentityChanged,

    /// <summary>需管理员（权限二分归 T-10；T-09 不产出此值）。</summary>
    NeedsElevation,

    /// <summary>被拦截（T-09：结束失败按机械事实出本值+错误码；T-10 权限二分后为本值或 NeedsElevation）。</summary>
    Blocked,

    /// <summary>保护名单跳过（自身命中或连带根因为保护名单）。</summary>
    SkippedProtected,

    /// <summary>白名单跳过（自身命中或连带根因为白名单）。</summary>
    SkippedWhitelisted,
}

/// <summary>释放逐项结果。Reason 人读；ErrorCode = Win32 错误码（仅结束失败类携带）。</summary>
public record ReleaseItemResult(
    int Pid,
    string Name,
    string? ExecutablePath,
    string? CommandLine,
    ReleaseItemOutcome Outcome,
    string? Reason = null,
    int? ErrorCode = null);

/// <summary>
/// 释放报告（契约 §1.3）。T-09 填充：ReleaseId/RequestedAtUtc/StartedAtUtc/FinishedAtUtc/Items
/// （Items 按 Pid 升序确定性输出，每 Pid 恰一项）。
/// Before/After/MainReleasedBytes/CheckReleasedBytes 归 T-10 填充（采样与双释放量为 T-10 WBS 范围，
/// T-09 以 null 表达未填充；LogPersisted 由 App 编排调日志后回填，null=未尝试）。
/// </summary>
public record ReleaseReport(
    Guid ReleaseId,
    DateTime RequestedAtUtc,
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    IReadOnlyList<ReleaseItemResult> Items,
    MemoryOverview? Before = null,
    MemoryOverview? After = null,
    long? MainReleasedBytes = null,
    long? CheckReleasedBytes = null,
    bool? LogPersisted = null)
{
    public IReadOnlyList<ReleaseItemResult> Items { get; init; } =
        Items == null ? Array.Empty<ReleaseItemResult>() : Items.ToArray();
}
