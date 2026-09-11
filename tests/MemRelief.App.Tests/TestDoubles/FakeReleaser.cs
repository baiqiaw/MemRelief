using MemRelief.Core.Contracts;
using MemRelief.Core.Releaser;

namespace MemRelief.App.Tests.TestDoubles;

/// <summary>释放域假实现（T-10 App 侧测试）：仅承载取消计数；Plan/Execute 非 T-10 消费面。</summary>
internal sealed class FakeReleaser : IReleaser
{
    public int CancelCalls { get; private set; }

    public void Cancel() => CancelCalls++;

#pragma warning disable CS0067 // 事件未触发（假实现：T-10 不经事件驱动编排）
    public event Action<int, TreeState>? TreeProgress;
    public event Action<ReleaseReport>? ReleaseCompleted;
#pragma warning restore CS0067

    public Task<IReadOnlyList<TreePlan>> Plan(
        ReleaseRequest request, ScanResult scan, WhitelistSnapshot whitelist, RulePack rulePack) =>
        throw new NotSupportedException("Plan 非 T-10 App 侧消费面");

    public Task<ReleaseReport> Execute(ReleaseRequest request, IReadOnlyList<TreePlan> plans) =>
        throw new NotSupportedException("Execute 接线归 T-16");
}
