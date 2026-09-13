using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;

namespace MemRelief.Bench.Tests;

/// <summary>四步链编排与分段计时（scanner.md §4.3 编排方时序 + system-spec §7 分段预算承载）。</summary>
public class BenchRunnerTests
{
    private static ClassificationContext Ctx() => new(9999, "tester");

    private static BenchRunner Create(CallLog log, FakeScanner scanner, FakeRules rules) => new(
        scanner, rules,
        rulePackProvider: () => new RulePackLoadResult(RulePack.Empty, []),
        whitelistProvider: () => new WhitelistSnapshot([]),
        context: Ctx());

    [Fact]
    public async Task 四步调用顺序_快照_候选_验签_判定()
    {
        var log = new CallLog();
        var runner = Create(log, new FakeScanner(log), new FakeRules(log));

        await runner.RunAsync();

        Assert.Equal(
            ["TakeSnapshot", "CandidateIds", "CollectSignatures", "Classify"],
            log.Snapshot());
    }

    [Fact]
    public async Task 段计时归位_采集段延迟计入采集不含判定()
    {
        var log = new CallLog();
        var scanner = new FakeScanner(log) { SnapshotDelayMs = 80 };
        var runner = Create(log, scanner, new FakeRules(log));

        var result = await runner.RunAsync();

        Assert.True(result.SnapshotMs >= 50, $"采集段应承载延迟，实测 {result.SnapshotMs}ms");
        Assert.True(result.ClassifyMs < result.SnapshotMs, "判定段不应吞入采集段耗时");
        Assert.True(result.TotalMs >= result.SnapshotMs, "总耗时不小于任一段");
    }

    [Fact]
    public async Task 段计时归位_验签段延迟计入验签段()
    {
        var log = new CallLog();
        var scanner = new FakeScanner(log) { VerifyDelayMs = 80 };
        var runner = Create(log, scanner, new FakeRules(log));

        var result = await runner.RunAsync();

        Assert.True(result.VerifyMs >= 50, $"验签段应承载延迟，实测 {result.VerifyMs}ms");
        Assert.True(result.SnapshotMs < result.VerifyMs, "采集段不应吞入验签段耗时");
    }

    [Fact]
    public async Task 段计时归位_判定段延迟计入判定段()
    {
        var log = new CallLog();
        var rules = new FakeRules(log) { ClassifyDelayMs = 80 };
        var runner = Create(log, new FakeScanner(log), rules);

        var result = await runner.RunAsync();

        Assert.True(result.ClassifyMs >= 50, $"判定段应承载延迟，实测 {result.ClassifyMs}ms");
    }

    [Fact]
    public async Task 引擎合计等于四段之和()
    {
        var log = new CallLog();
        var scanner = new FakeScanner(log) { VerifyDelayMs = 20 };
        var runner = Create(log, scanner, new FakeRules(log));

        var result = await runner.RunAsync();

        Assert.Equal(
            result.SnapshotMs + result.CandidateIdsMs + result.VerifyMs + result.ClassifyMs,
            result.EngineMs);
    }

    [Fact]
    public async Task 空快照_零进程_正常产出不抛()
    {
        var log = new CallLog();
        var scanner = new FakeScanner(log);   // 默认返回空快照
        var runner = Create(log, scanner, new FakeRules(log));

        var result = await runner.RunAsync();

        Assert.Equal(0, result.Snapshot.ProcessCount);
        Assert.Empty(result.Classifications);
        Assert.Empty(result.TimingAnomalies());
    }

    [Fact]
    public async Task 正常路径_无计时异常()
    {
        var log = new CallLog();
        var scanner = new FakeScanner(log)
        {
            SnapshotDelayMs = 10,
            VerifyDelayMs = 5,
            SnapshotFactory = SampleResults.TwoProcesses,
        };
        var rules = new FakeRules(log)
        {
            ClassifyDelayMs = 5,
            ClassifyOutput = SampleResults.TwoClassifications(),
        };
        var runner = Create(log, scanner, rules);

        var result = await runner.RunAsync();

        Assert.Empty(result.TimingAnomalies());
        Assert.Equal(2, result.Snapshot.ProcessCount);   // SampleResults.TwoProcesses
        Assert.Equal(2, result.Classifications.Count);
    }
}
