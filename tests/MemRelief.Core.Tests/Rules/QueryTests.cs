using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using Xunit;

namespace MemRelief.Core.Tests.Rules;

// 全量判定查询单测（R02 搜索框）：复用 Classify 全量输出定位，不重算（rules.md §4.1）
public class QueryTests
{
    // 编排链实测：Classify → Query（对齐 scanner §4.3 产物流）
    private static async Task<(ScanResult Scan, IReadOnlyList<Classification> All)> ScanAsync(
        params ProcessSnapshot[] snapshots)
    {
        var scan = Snap.Scan(snapshots);
        var all = await new RulesEngine().Classify(scan, Snap.Whitelist(), Snap.Pack(), Snap.Ctx());
        return (scan, all);
    }

    [Fact]
    public async Task 按名查_推荐级_Bases与Classify一致()
    {
        var (scan, all) = await ScanAsync(Snap.Clean(1, name: "app.exe"), Snap.Clean(2, name: "other.exe"));
        var hits = new RulesEngine().Query(scan, all, "app.exe", null);
        var hit = Assert.Single(hits);
        Assert.Equal(1, hit.Pid);
        Assert.Equal("app.exe", hit.Name);
        Assert.Equal(Level.Recommend, hit.Outcome);
        var expected = Assert.Single(all, c => c.Pid == 1);
        Assert.Equal(expected.Bases.Select(b => (b.SignalId, b.Detail)),
            hit.Bases.Select(b => (b.SignalId, b.Detail)));
    }

    [Fact]
    public async Task 按PID查()
    {
        var (scan, all) = await ScanAsync(Snap.Clean(1), Snap.Clean(2));
        var hit = Assert.Single(new RulesEngine().Query(scan, all, null, 2));
        Assert.Equal(2, hit.Pid);
    }

    [Fact]
    public async Task Pid优先于名()
    {
        var (scan, all) = await ScanAsync(Snap.Clean(1, name: "app.exe"), Snap.Clean(2, name: "other.exe"));
        var hit = Assert.Single(new RulesEngine().Query(scan, all, "other.exe", 1));
        Assert.Equal(1, hit.Pid);
        Assert.Equal("app.exe", hit.Name);
    }

    [Fact]
    public async Task 同名多进程_全部返回()
    {
        var (scan, all) = await ScanAsync(Snap.Clean(1, name: "worker.exe"), Snap.Clean(2, name: "worker.exe"));
        var hits = new RulesEngine().Query(scan, all, "worker.exe", null).OrderBy(h => h.Pid).ToArray();
        Assert.Equal(new[] { 1, 2 }, hits.Select(h => h.Pid));
        Assert.All(hits, h => Assert.Equal("worker.exe", h.Name));
    }

    [Fact]
    public async Task 名称大小写不敏感()
    {
        var (scan, all) = await ScanAsync(Snap.Clean(1, name: "app.exe"));
        var hit = Assert.Single(new RulesEngine().Query(scan, all, "APP.EXE", null));
        Assert.Equal(1, hit.Pid);
    }

    [Fact]
    public async Task 白名单进程_白名单排除()
    {
        var scan = Snap.Scan(Snap.Clean(1, name: "keepme.exe"));
        var all = await new RulesEngine().Classify(scan, Snap.Whitelist("keepme.exe"), Snap.Pack(), Snap.Ctx());
        var hit = Assert.Single(new RulesEngine().Query(scan, all, "keepme.exe", null));
        Assert.Equal(Level.Whitelisted, hit.Outcome);
    }

    [Fact]
    public async Task 未命中规则进程_未命中规则()
    {
        // v1 引擎 Classify 结构上不产生 Unmatched（每进程至少一条依据）；
        // 本条直测 Query 对全量集的映射层——Outcome=Unmatched 即 PRD F2"未命中规则"
        var scan = Snap.Scan(Snap.Clean(1));
        var all = new Classification[]
        {
            new(1, Level.Unmatched, Array.Empty<Basis>(), 0, null, Array.Empty<SourceEntry>(), false),
        };
        var hit = Assert.Single(new RulesEngine().Query(scan, all, "app.exe", null));
        Assert.Equal(Level.Unmatched, hit.Outcome);
        Assert.Empty(hit.Bases);
    }

    [Fact]
    public async Task 查无此名_空集()
    {
        var (scan, all) = await ScanAsync(Snap.Clean(1));
        Assert.Empty(new RulesEngine().Query(scan, all, "ghost.exe", null));
        Assert.Empty(new RulesEngine().Query(scan, all, null, 99));
    }

    [Fact]
    public async Task 名与Pid均空_空集()
    {
        var (scan, all) = await ScanAsync(Snap.Clean(1));
        Assert.Empty(new RulesEngine().Query(scan, all, null, null));
    }

    [Fact]
    public async Task 受保护进程_查得原因()
    {
        var (scan, all) = await ScanAsync(Snap.Clean(1, name: "svchost.exe"));
        var hit = Assert.Single(new RulesEngine().Query(scan, all, "svchost.exe", null));
        Assert.Equal(Level.Protected, hit.Outcome);
        Assert.NotEmpty(hit.Bases);
    }
}
