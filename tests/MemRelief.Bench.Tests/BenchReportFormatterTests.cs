using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;

namespace MemRelief.Bench.Tests;

/// <summary>报告渲染（纯函数）：分级明细/判定依据/分段计时对照/对抗性输入防御。</summary>
public class BenchReportFormatterTests
{
    private static BenchResult Result(
        long snapshotMs = 100, long candidateIdsMs = 0, long verifyMs = 50, long classifyMs = 30)
    {
        var scan = SampleResults.TwoProcesses();
        return new BenchResult(
            scan, SampleResults.TwoClassifications(),
            snapshotMs, candidateIdsMs, verifyMs, classifyMs,
            TotalMs: snapshotMs + candidateIdsMs + verifyMs + classifyMs + 7);
    }

    private static string Format(BenchResult result, int topN = 20) =>
        BenchReportFormatter.Format(result, new BenchEnvironment("TEST-MACHINE",
            new DateTime(2026, 9, 13, 12, 0, 0)), topN);

    [Fact]
    public void 头部含采样时间与机器名与进程总数()
    {
        var text = Format(Result());

        Assert.Contains("TEST-MACHINE", text);
        Assert.Contains("2026-09-13 12:00:00", text);
        Assert.Contains("2", text);   // 快照进程总数
    }

    [Fact]
    public void 分段计时表含四段与预算对照及引擎合计()
    {
        var text = Format(Result(snapshotMs: 1200, verifyMs: 300, classifyMs: 100));

        Assert.Contains("采集", text);
        Assert.Contains("1200", text);
        Assert.Contains("2000", text);    // 采集预算
        Assert.Contains("验签", text);
        Assert.Contains("500", text);     // 验签预算
        Assert.Contains("判定", text);
        Assert.Contains("300", text);     // 判定预算
        Assert.Contains("引擎合计", text);
        Assert.Contains("2800", text);    // 引擎合计预算
        Assert.Contains("达标", text);
    }

    [Fact]
    public void 判定段对照值并入候选预筛耗时()
    {
        // 口径：判定段预算对照值 = ClassifyMs + CandidateIdsMs（spec §7 预筛并入判定段，无独立预算）
        var text = Format(Result(candidateIdsMs: 7, classifyMs: 30));

        Assert.Contains("37 ms / 预算 300 ms", text);
    }

    [Fact]
    public void 超预算段标记超标不隐藏()
    {
        var text = Format(Result(snapshotMs: 2500));

        Assert.Contains("超标", text);
    }

    [Fact]
    public void 分级统计含五级计数()
    {
        var text = Format(Result());

        // 2 进程样本：1 推荐 + 1 受保护
        Assert.Contains("推荐", text);
        Assert.Contains("受保护", text);
        Assert.Contains("白名单", text);
        Assert.Contains("未匹配", text);
    }

    [Fact]
    public void 推荐明细含进程名路径与判定依据编号()
    {
        var text = Format(Result());

        Assert.Contains("orphan.exe", text);
        Assert.Contains(@"C:\app\orphan.exe", text);
        Assert.Contains("#1", text);   // 口径编号可追溯
    }

    [Fact]
    public void 受保护级进Top明细含判定依据()
    {
        var text = Format(Result());

        Assert.Contains("guarded.exe", text);
        Assert.Contains("#10", text);
    }

    [Fact]
    public void TopN截断_top为0_明细省略但统计保留()
    {
        var text = Format(Result(), topN: 0);

        Assert.DoesNotContain("guarded.exe", text);   // 明细行省略
        Assert.Contains("受保护", text);               // 分级统计保留计数
    }

    [Fact]
    public void 空快照渲染不抛且显示零进程()
    {
        var empty = new BenchResult(
            SampleResults.Empty(), [],
            10, 0, 0, 5, TotalMs: 15);

        var text = Format(empty);

        Assert.Contains("0", text);
        Assert.Contains("引擎合计", text);
    }

    [Fact]
    public void 负值段耗时标记异常不抛()
    {
        // 对抗性输入：秒表异常/时钟回退的防御路径——负值注入真实触发
        var anomalous = new BenchResult(
            SampleResults.TwoProcesses(), SampleResults.TwoClassifications(),
            -5, 0, 50, 30, TotalMs: 75);

        Assert.NotEmpty(anomalous.TimingAnomalies());

        var text = Format(anomalous);
        Assert.Contains("异常", text);
    }

    [Fact]
    public void 总耗时段矛盾_总小于引擎合计_标记异常()
    {
        var anomalous = new BenchResult(
            SampleResults.TwoProcesses(), SampleResults.TwoClassifications(),
            100, 0, 50, 30, TotalMs: 10);

        Assert.NotEmpty(anomalous.TimingAnomalies());
    }

    [Fact]
    public void 候选预筛负值段耗时标记异常()
    {
        var anomalous = new BenchResult(
            SampleResults.TwoProcesses(), SampleResults.TwoClassifications(),
            100, -1, 50, 30, TotalMs: 179);

        Assert.Contains(anomalous.TimingAnomalies(), s => s.Contains("候选预筛"));
    }

    [Fact]
    public void 总耗时负值标记异常()
    {
        var anomalous = new BenchResult(
            SampleResults.TwoProcesses(), SampleResults.TwoClassifications(),
            100, 0, 50, 30, TotalMs: -5);

        Assert.Contains(anomalous.TimingAnomalies(), s => s.Contains("总耗时"));
    }

    [Fact]
    public void 后续轮计时表含轮次与缓存热态标注()
    {
        var text = BenchReportFormatter.FormatTimings(
            Result(), new BenchEnvironment("TEST-MACHINE", new DateTime(2026, 9, 13, 12, 0, 0)), 2);

        Assert.Contains("第 2 轮", text);
        Assert.Contains("缓存热态", text);
        Assert.Contains("引擎合计", text);
    }

    [Fact]
    public void 采集失败摘要含分级计数()
    {
        var text = Format(Result());

        Assert.Contains("采集失败", text);
        Assert.Contains("AccessDenied", text);
    }
}
