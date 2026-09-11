using MemRelief.Core.Contracts;

namespace MemRelief.Core.Releaser;

/// <summary>
/// 释放域对外接口（releaser.md §5）：Plan/Execute/Cancel + 事件 TreeProgress/ReleaseCompleted。
/// 树构建唯一承载 = TreePlanner（Plan 委托）；Execute 复用 TreePlan 输出不重建树（T-08 契约）。
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
    /// 报告含 Before/After 内存采样（Execute 进入时/全部树终态后，③.s4 裁决⑤）与双释放量
    /// （主释放量=被结束进程快照私有提交合计，校验释放量=commit 前后差可负如实输出）。
    /// 取消经 <see cref="Cancel"/>：取消收尾仍产出完整报告与完成事件。
    /// </summary>
    Task<ReleaseReport> Execute(ReleaseRequest request, IReadOnlyList<TreePlan> plans);

    /// <summary>
    /// 取消（PRD F3-5，T-10）：跳过所有未开始的树，进行中的树等待收尾——不得中断进行中的强杀
    /// （防半完成态，releaser.md §6 法条）。幂等；空闲时调用为无操作（不污染下一次 Execute）。
    /// 取消收尾的释放照常发出 <see cref="ReleaseCompleted"/>（至多一次），报告记已执行部分（PRD F3-6）。
    /// </summary>
    void Cancel();

    /// <summary>树级进度（执行工作线程发出，ui 负责编组——data-contracts §1.5 线程亲和性）。</summary>
    event Action<int, TreeState>? TreeProgress;

    /// <summary>释放完成（至多一次，执行工作线程发出；App 编排据此调日志追加）。</summary>
    event Action<ReleaseReport>? ReleaseCompleted;
}
