using MemRelief.Core.Contracts;

namespace MemRelief.Core.Releaser;

/// <summary>
/// 释放域对外接口（releaser.md §5）：Plan/Execute + 事件 TreeProgress/ReleaseCompleted。
/// 树构建唯一承载 = TreePlanner（Plan 委托）；Execute 复用 TreePlan 输出不重建树（T-08 契约）。
/// Cancel 归 T-10（WBS T-10 范围：跳过未开始+等待进行中；T-09 先交主链，避免未实现占位）。
/// </summary>
public interface IReleaser
{
    /// <summary>
    /// 规划：树构建 + 保护集标记 + 树合计内存（纯函数承载 TreePlanner）。
    /// 结果供确认弹窗展示与 <see cref="Execute"/> 复用。
    /// </summary>
    Task<IReadOnlyList<TreePlan>> Plan(
        ReleaseRequest request,
        ScanResult scan,
        WhitelistSnapshot whitelist,
        RulePack rulePack);

    /// <summary>
    /// 执行：多树并行两段式结束——每树身份校验 → 优雅（WM_CLOSE，无窗口项跳过）→ 3s → TerminateProcess
    /// （releaser.md §4.1）；单树 ≤5s（含 3s 优雅等待，PRD §3.4）。返回逐项结果报告；
    /// Before/After 采样与双释放量归 T-10 填充。取消语义随 T-10 接入。
    /// </summary>
    Task<ReleaseReport> Execute(ReleaseRequest request, IReadOnlyList<TreePlan> plans);

    /// <summary>树级进度（执行工作线程发出，ui 负责编组——data-contracts §1.5 线程亲和性）。</summary>
    event Action<int, TreeState>? TreeProgress;

    /// <summary>释放完成（至多一次，执行工作线程发出；App 编排据此调日志追加）。</summary>
    event Action<ReleaseReport>? ReleaseCompleted;
}
