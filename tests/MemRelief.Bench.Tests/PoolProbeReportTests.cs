namespace MemRelief.Bench.Tests;

/// <summary>poolprobe 报告格式化：表头、行内容与 SLA 对照标记。</summary>
public class PoolProbeReportTests
{
    private static readonly BenchEnvironment Env = new("TEST-MACHINE", new DateTime(2026, 10, 5, 12, 0, 0));

    [Fact]
    public void 表头含列名与环境行()
    {
        var text = PoolProbeReport.Format(
        [
            new PoolProbeResult(10, "sync", 3_050, 8, 12),
        ], Env, waitMs: 3000, pollMs: 100);

        Assert.Contains("树数", text);
        Assert.Contains("模式", text);
        Assert.Contains("整批耗时", text);
        Assert.Contains("TEST-MACHINE", text);
    }

    [Fact]
    public void 行含两模式耗时与线程数观测()
    {
        var text = PoolProbeReport.Format(
        [
            new PoolProbeResult(10, "sync", 3_050, 8, 12),
            new PoolProbeResult(10, "async", 3_010, 12, 12),
        ], Env, waitMs: 3000, pollMs: 100);

        Assert.Contains("sync", text);
        Assert.Contains("3050", text);
        Assert.Contains("async", text);
        Assert.Contains("3010", text);
        Assert.Contains("8→12", text);
    }

    [Fact]
    public void SLA对照_超30s标未达标_30s内标达标()
    {
        var text = PoolProbeReport.Format(
        [
            new PoolProbeResult(10, "sync", 3_050, 8, 12),
            new PoolProbeResult(300, "sync", 45_000, 8, 280),
        ], Env, waitMs: 3000, pollMs: 100);

        Assert.Contains("3050", text);
        Assert.Contains("45000", text);
        // 30s SLA 对照标记（PRD §3.4 整批 ≤30s）
        Assert.Contains("✅", text);
        Assert.Contains("❌", text);
    }
}
