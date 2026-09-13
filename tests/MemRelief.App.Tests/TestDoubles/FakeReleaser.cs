using MemRelief.Core.Contracts;
using MemRelief.Core.Releaser;

namespace MemRelief.App.Tests.TestDoubles;

/// <summary>
/// 释放域假实现（App 侧测试）：记录 Plan/Execute/Cancel 调用；事件经 Raise 方法由测试显式驱动
/// （生产事件顺序：ReleaseCompleted 在 Execute 返回前于工作线程发出——Core 实证，假实现同序镜像）。
/// </summary>
internal sealed class FakeReleaser : IReleaser
{
    public int CancelCalls { get; private set; }

    public int PlanCalls { get; private set; }

    public List<ReleaseRequest> ExecutedRequests { get; } = [];

    public List<IReadOnlyList<TreePlan>> ExecutedPlans { get; } = [];

    /// <summary>Plan 返回的计划（默认空=无可释放）。</summary>
    public IReadOnlyList<TreePlan> PlanResult { get; set; } = [];

    /// <summary>Execute 定制（默认：完成事件后返回空项报告）。</summary>
    public Func<ReleaseRequest, IReadOnlyList<TreePlan>, Task<ReleaseReport>>? OnExecute { get; set; }

    /// <summary>注入后 Plan 抛出该异常（规划失败链，如保护名单不可用 fail-closed）。</summary>
    public Exception? OnPlanError { get; set; }

    public void Cancel() => CancelCalls++;

    public Task<IReadOnlyList<TreePlan>> Plan(
        ReleaseRequest request, ScanResult scan, WhitelistSnapshot whitelist, RulePack rulePack)
    {
        PlanCalls++;
        if (OnPlanError is not null)
        {
            throw OnPlanError;
        }

        return Task.FromResult(PlanResult);
    }

    public async Task<ReleaseReport> Execute(ReleaseRequest request, IReadOnlyList<TreePlan> plans)
    {
        ExecutedRequests.Add(request);
        ExecutedPlans.Add(plans);
        var report = OnExecute is not null
            ? await OnExecute(request, plans)
            : new ReleaseReport(request.ReleaseId, request.RequestedAtUtc, DateTime.UtcNow, DateTime.UtcNow, []);
        RaiseCompleted(report); // 与 Core 同序：事件先于 Execute 返回
        return report;
    }

    public void RaiseTreeProgress(int rootPid, TreeState state) => TreeProgress?.Invoke(rootPid, state);

    public void RaiseCompleted(ReleaseReport report) => ReleaseCompleted?.Invoke(report);

    public event Action<int, TreeState>? TreeProgress;
    public event Action<ReleaseReport>? ReleaseCompleted;
}
