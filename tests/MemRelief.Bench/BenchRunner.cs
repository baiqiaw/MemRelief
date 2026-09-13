using System.Diagnostics;
using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using MemRelief.Core.Scanner;

namespace MemRelief.Bench;

/// <summary>
/// 四步扫描链验证编排（scanner.md §4.3 编排方时序 + system-spec §7 分段计时承载）：
/// TakeSnapshot → CandidateIds → CollectSignatures → Classify，各段独立秒表。
/// 名单装载失败兜底与 App 侧 ScanCoordinator 同款（RulePack.Empty，system 法-3）——
/// 本类只做驱动与计时，不含判定/采集逻辑（T-21 边界）。
/// 依赖以接口注入（IScanner/IRulesEngine），单测以替身承载编排顺序与计时归位断言。
/// </summary>
public sealed class BenchRunner(
    IScanner scanner,
    IRulesEngine rules,
    Func<RulePackLoadResult> rulePackProvider,
    Func<WhitelistSnapshot> whitelistProvider,
    ClassificationContext context)
{
    public async Task<BenchResult> RunAsync()
    {
        var total = Stopwatch.StartNew();

        var sw = Stopwatch.StartNew();
        var snapshot = await scanner.TakeSnapshot().ConfigureAwait(false);
        var snapshotMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var candidates = rules.CandidateIds(snapshot);
        var candidateIdsMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var verified = await scanner.CollectSignatures(snapshot, candidates).ConfigureAwait(false);
        var verifyMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var packResult = rulePackProvider();
        var pack = packResult.Failures.Count > 0 ? RulePack.Empty : packResult.Pack;
        var classifications = await rules
            .Classify(verified, whitelistProvider(), pack, context).ConfigureAwait(false);
        var classifyMs = sw.ElapsedMilliseconds;

        total.Stop();
        return new BenchResult(
            verified, classifications,
            snapshotMs, candidateIdsMs, verifyMs, classifyMs,
            total.ElapsedMilliseconds);
    }
}
