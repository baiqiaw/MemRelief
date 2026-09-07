using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using Xunit;

namespace MemRelief.Core.Tests.Rules;

// 跨用户预标单测（T-07）：所有者非当前用户 → RequiresElevation + 依据入 Bases，不改级。
// null 兜底方向见 data-contracts §2 T-07 裁决：任一不可读 → 不预标（释放分类执行期兜底）。
public class ClassifyElevationTests
{
    private static async Task<Dictionary<int, Classification>> Run(
        ScanResult scan, ClassificationContext? ctx = null)
    {
        var result = await new RulesEngine().Classify(scan, Snap.Whitelist(), Snap.Pack(), ctx ?? Snap.Ctx());
        return result.ToDictionary(c => c.Pid);
    }

    [Fact]
    public async Task 跨用户进程_预标且依据入Bases()
    {
        var snap = Snap.Clean(1) with { OwnerUser = "someone" };
        var byId = await Run(Snap.Scan(snap));
        Assert.True(byId[1].RequiresElevation);
        var basis = Assert.Single(byId[1].Bases, b => b.SignalId == 0 && b.Detail.Contains("跨用户"));
        Assert.Contains("someone", basis.Detail);
    }

    [Fact]
    public async Task 本用户进程_不预标()
    {
        var snap = Snap.Clean(1) with { OwnerUser = "tester" };
        var byId = await Run(Snap.Scan(snap));
        Assert.False(byId[1].RequiresElevation);
    }

    [Fact]
    public async Task 用户名大小写差异_视为同人()
    {
        var snap = Snap.Clean(1) with { OwnerUser = "Tester" };
        var byId = await Run(Snap.Scan(snap));
        Assert.False(byId[1].RequiresElevation);
    }

    [Fact]
    public async Task 当前用户不可读_不预标()
    {
        // CurrentUserName=null（获取失败）→ 不可判不预标（裁决③）
        var snap = Snap.Clean(1) with { OwnerUser = "someone" };
        var byId = await Run(Snap.Scan(snap), ctx: new ClassificationContext(9999, null));
        Assert.False(byId[1].RequiresElevation);
        Assert.DoesNotContain(byId[1].Bases, b => b.Detail.Contains("跨用户"));
    }

    [Fact]
    public async Task 所有者不可读_不预标()
    {
        var snap = Snap.Clean(1) with { OwnerUser = null };
        var byId = await Run(Snap.Scan(snap));
        Assert.False(byId[1].RequiresElevation);
    }

    [Fact]
    public async Task 预标不改级()
    {
        // 预标只是标记：clean 进程仍✅，预标不参与冲突消解
        var snap = Snap.Clean(1) with { OwnerUser = "someone" };
        var byId = await Run(Snap.Scan(snap));
        Assert.Equal(Level.Recommend, byId[1].Level);
        Assert.True(byId[1].RequiresElevation);
    }

    [Fact]
    public async Task 服务与跨用户_预标叠加()
    {
        // 服务[口径#8]与跨用户同时命中：RequiresElevation=true，双依据入 Bases
        var signals = Snap.CleanSignals() with { ServiceName = "SvcA", ServiceRestartOnFailure = true };
        var snap = Snap.Clean(1, signals: signals) with { OwnerUser = "someone" };
        var byId = await Run(Snap.Scan(snap));
        Assert.True(byId[1].RequiresElevation);
        Assert.Contains(byId[1].Bases, b => b.SignalId == 8);
        Assert.Contains(byId[1].Bases, b => b.SignalId == 0 && b.Detail.Contains("跨用户"));
    }
}
