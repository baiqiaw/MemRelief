using MemRelief.App.Scanning;
using MemRelief.App.Tests.TestDoubles;
using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;
using MemRelief.Core.Storage;

namespace MemRelief.App.Tests.Scanning;

/// <summary>
/// 扫描链四步编排测试（scanner.md §4.3 时序：TakeSnapshot → CandidateIds → CollectSignatures → Classify）。
/// AC-2：编排失败收口为失败结果（VM 层保留旧结果、不展示半成品）。
/// </summary>
public class ScanCoordinatorTests
{
    private sealed class FakePackStore(FakePackStore.FailuresMode mode) : IRulePackStore
    {
        public enum FailuresMode { Clean, Broken }

        public RulePackLoadResult LoadRulePack() => mode switch
        {
            FailuresMode.Clean => new RulePackLoadResult(
                new RulePack(["crashpad"], ["wechat"], [], []), []),
            // 真实 RulePackStore 语义：失败名单置空数组、其余名单保留（残包）+ Failures 非空；
            // 兜底映射（残包→Empty）是编排方职责，替身不得代行
            FailuresMode.Broken => new RulePackLoadResult(
                new RulePack(["crashpad"], ["wechat"], [], []),
                [new RulePackLoadFailure(RulePackList.SecurityApps, "资源缺失")]),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private static ScanCoordinator NewCoordinator(
        FakeScanner? scanner = null, FakeRules? rules = null, FakePackStore? store = null)
    {
        var context = new ClassificationContext(SelfPid: 1, CurrentUserName: "u");
        return new ScanCoordinator(
            scanner ?? new FakeScanner(),
            rules ?? new FakeRules(),
            store ?? new FakePackStore(FakePackStore.FailuresMode.Clean),
            context,
            () => new WhitelistSnapshot([]));
    }

    [Fact]
    public async Task 编排顺序_四步依次执行()
    {
        var scanner = new FakeScanner();
        var rules = new FakeRules();
        var coordinator = NewCoordinator(scanner, rules);

        var run = await coordinator.RunAsync();

        Assert.True(run.Success);
        Assert.Equal(
            [nameof(IScanner.TakeSnapshot), nameof(IScanner.CollectSignatures)],
            scanner.Calls); // CandidateIds/Classify 是 rules 调用，经 ClassifyCalls 佐证
        Assert.Single(rules.ClassifyCalls);
    }

    [Fact]
    public async Task 候选集_从CandidateIds传入CollectSignatures()
    {
        var scanner = new FakeScanner();
        var coordinator = NewCoordinator(scanner);

        await coordinator.RunAsync();

        Assert.NotNull(scanner.ReceivedCandidates);
        Assert.Equal(new HashSet<int> { 100 }, scanner.ReceivedCandidates);
    }

    [Fact]
    public async Task 验签回填产物_作为Classify输入()
    {
        var baseSnapshot = FakeScanner.DefaultSnapshot;
        var signed = baseSnapshot with
        {
            Snapshots =
            [
                baseSnapshot.Snapshots[0] with
                {
                    Signals = baseSnapshot.Snapshots[0].Signals with
                    {
                        SignatureStatus = SignatureStatus.Microsoft,
                    },
                },
            ],
        };
        var scanner = new FakeScanner
        {
            OnCollectSignatures = (_, _) => Task.FromResult(signed),
        };
        var rules = new FakeRules();
        var coordinator = NewCoordinator(scanner, rules);

        await coordinator.RunAsync();

        Assert.Equal(signed, rules.ClassifyCalls[0].Scan); // 判定消费验签回填后的快照，非原始快照
    }

    [Fact]
    public async Task 成功_输出快照与全量分类()
    {
        var coordinator = NewCoordinator();

        var run = await coordinator.RunAsync();

        Assert.True(run.Success);
        Assert.NotNull(run.Outcome);
        Assert.Equal(FakeScanner.DefaultSnapshot, run.Outcome.Snapshot);
        Assert.Single(run.Outcome.Classifications);
    }

    [Fact]
    public async Task 名单加载失败_传空包_扫描不阻断()
    {
        // system 法-3：保护性名单加载失败 → 引擎侧不进✅，编排侧职责=传空包
        var rules = new FakeRules();
        var coordinator = NewCoordinator(
            rules: rules, store: new FakePackStore(FakePackStore.FailuresMode.Broken));

        var run = await coordinator.RunAsync();

        Assert.True(run.Success);
        Assert.Equal(RulePack.Empty, rules.ClassifyCalls[0].Pack);
    }

    [Fact]
    public async Task 白名单快照_参数注入引擎()
    {
        var rules = new FakeRules();
        var whitelist = new WhitelistSnapshot(
            [new WhitelistEntry("keep.exe", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc))]);
        var context = new ClassificationContext(1, "u");
        var coordinator = new ScanCoordinator(
            new FakeScanner(), rules, new FakePackStore(FakePackStore.FailuresMode.Clean),
            context, () => whitelist);

        await coordinator.RunAsync();

        Assert.Single(rules.ClassifyCalls); // 白名单经 Classify 入参注入（编排方装载，rules 零 I/O）
    }

    // —— T-15 加白即时性（③.s4 裁决⑥）：加白后重跑 Classify 的编排入口 ——

    [Fact]
    public async Task 重跑判定_传入指定快照_白名单取提供者当前视图()
    {
        var rules = new FakeRules();
        var whitelist = new FakeWhitelistStore();
        var context = new ClassificationContext(1, "u");
        var coordinator = new ScanCoordinator(
            new FakeScanner(), rules, new FakePackStore(FakePackStore.FailuresMode.Clean),
            context, () => whitelist.Snapshot());

        whitelist.Add("b.exe");
        await coordinator.ReclassifyAsync(FakeScanner.DefaultSnapshot);

        var call = Assert.Single(rules.ClassifyCalls);
        Assert.Equal(FakeScanner.DefaultSnapshot, call.Scan);
        Assert.NotNull(call.Pack);
        // 提供者返回 Add 后的最新快照（单条目），非构造期固定值——白名单实参逐字断言
        var entry = Assert.Single(call.Whitelist.Entries);
        Assert.Equal("b.exe", entry.Name);
    }

    [Fact]
    public async Task 重跑判定_名单加载失败_同扫描链传空包()
    {
        // 装载规则单点：Reclassify 与 RunAsync 共用（残包→Empty 兜底不因入口不同而分叉）
        var rules = new FakeRules();
        var whitelist = new FakeWhitelistStore();
        var coordinator = new ScanCoordinator(
            new FakeScanner(), rules, new FakePackStore(FakePackStore.FailuresMode.Broken),
            new ClassificationContext(1, "u"), () => whitelist.Snapshot());

        await coordinator.ReclassifyAsync(FakeScanner.DefaultSnapshot);

        Assert.Equal(RulePack.Empty, rules.ClassifyCalls.Single().Pack);
    }

    [Theory]
    [InlineData(1)] // TakeSnapshot 失败
    [InlineData(2)] // CollectSignatures 失败（T-04 未实装抛 NotImplemented 即走此路径）
    public async Task 任一步异常_失败收口_携带原因_不向上抛(int failAt)
    {
        var scanner = new FakeScanner();
        if (failAt == 1)
        {
            scanner.OnTakeSnapshot = () => throw new InvalidOperationException("枚举整体失败");
        }
        else
        {
            scanner.OnCollectSignatures = (_, _) => throw new NotImplementedException("T-04 未实装");
        }

        var coordinator = NewCoordinator(scanner);

        var run = await coordinator.RunAsync(); // 不抛：失败是可预期结果路径

        Assert.False(run.Success);
        Assert.Null(run.Outcome);
        Assert.NotEmpty(run.FailureReason!);
        Assert.Contains(failAt == 1 ? "枚举整体失败" : "T-04 未实装", run.FailureReason);
    }

    [Fact]
    public async Task 异常消息为空_失败原因回退异常类型名()
    {
        // 真机 COM/WMI 包装异常存在 Message 空串形态，裸 ex.Message 会让用户看到“扫描失败：”无原因
        var scanner = new FakeScanner
        {
            OnTakeSnapshot = () => throw new InvalidOperationException(),
        };

        var run = await NewCoordinator(scanner).RunAsync();

        Assert.False(run.Success);
        Assert.Contains(nameof(InvalidOperationException), run.FailureReason!);
    }
}
