namespace MemRelief.Bench.Tests;

/// <summary>承载实验编排（真实微缩跑：极小 waitMs/pollMs 驱动真实调度路径，不用替身）。</summary>
public class PoolProbeTests
{
    [Fact]
    public async Task 微缩参数_两模式各一条记录()
    {
        var probe = new PoolProbe();
        var records = await probe.RunAsync([3], waitMs: 80, pollMs: 20, mode: "both");

        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.Mode == "sync");
        Assert.Contains(records, r => r.Mode == "async");
    }

    [Fact]
    public async Task mode为sync_只跑同步模式()
    {
        var probe = new PoolProbe();
        var records = await probe.RunAsync([3], waitMs: 60, pollMs: 20, mode: "sync");

        var record = Assert.Single(records);
        Assert.Equal("sync", record.Mode);
    }

    [Fact]
    public async Task mode为async_只跑异步模式()
    {
        var probe = new PoolProbe();
        var records = await probe.RunAsync([3], waitMs: 60, pollMs: 20, mode: "async");

        var record = Assert.Single(records);
        Assert.Equal("async", record.Mode);
    }

    [Fact]
    public async Task 每条记录_树数回填_耗时下界为waitMs()
    {
        var probe = new PoolProbe();
        var records = await probe.RunAsync([5], waitMs: 80, pollMs: 20, mode: "both");

        Assert.All(records, r => Assert.Equal(5, r.Trees));
        // 每任务至少等待 waitMs，WhenAll 整批耗时 ≥ waitMs（硬下界，无抖动敏感上界）
        Assert.All(records, r => Assert.True(r.TotalMs >= 80, $"整批耗时 {r.TotalMs}ms < waitMs 80ms"));
    }

    [Fact]
    public async Task 等待任务全部完成_两模式无丢任务()
    {
        var probe = new PoolProbe();
        var records = await probe.RunAsync([4], waitMs: 60, pollMs: 20, mode: "both");

        Assert.All(records, r => Assert.Equal(4, r.Trees));
    }

    [Fact]
    public async Task 多规模_每规模两模式_顺序为规模升序()
    {
        var probe = new PoolProbe();
        var records = await probe.RunAsync([2, 3], waitMs: 40, pollMs: 10, mode: "both");

        Assert.Equal(4, records.Count);
        Assert.Equal([2, 2, 3, 3], records.Select(r => r.Trees).ToArray());
    }
}
