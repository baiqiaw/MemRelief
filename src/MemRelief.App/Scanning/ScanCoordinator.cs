using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using MemRelief.Core.Scanner;
using MemRelief.Core.Storage;

namespace MemRelief.App.Scanning;

/// <summary>一次扫描编排的产物：验签回填后的快照 + 全量分类。</summary>
public record ScanOutcome(ScanResult Snapshot, IReadOnlyList<Classification> Classifications);

/// <summary>
/// 扫描编排结果（system §6 情报-1 结果模式）：失败是可预期路径，经返回值承载，不抛异常打断调用方。
/// </summary>
public record ScanRun(bool Success, ScanOutcome? Outcome, string? FailureReason)
{
    public static ScanRun Ok(ScanOutcome outcome) => new(true, outcome, null);

    public static ScanRun Fail(string reason) => new(false, null, reason);
}

/// <summary>
/// 扫描链四步编排（ui 模块组合根侧，scanner.md §4.3 时序图的编排方实现）：
/// TakeSnapshot → CandidateIds → CollectSignatures → Classify。
/// 名单/白名单由编排方装载后参数注入（rules 纯函数，零 I/O）；
/// 名单加载失败 → 传 RulePack.Empty 兜底（system 法-3，RulePackLoadResult 契约）。
/// 白名单以提供者注入（T-15 加白即时性，③.s4 裁决⑥）：每次判定取当前一致视图，
/// 加白成功后重跑 <see cref="ReclassifyAsync"/> 即生效。
/// </summary>
public sealed class ScanCoordinator
{
    private readonly IScanner _scanner;
    private readonly IRulesEngine _rules;
    private readonly IRulePackStore _rulePackStore;
    private readonly ClassificationContext _context;
    private readonly Func<WhitelistSnapshot> _whitelistProvider;

    public ScanCoordinator(
        IScanner scanner,
        IRulesEngine rules,
        IRulePackStore rulePackStore,
        ClassificationContext context,
        Func<WhitelistSnapshot> whitelistProvider)
    {
        _scanner = scanner;
        _rules = rules;
        _rulePackStore = rulePackStore;
        _context = context;
        _whitelistProvider = whitelistProvider;
    }

    /// <summary>执行一次四步扫描链；任一步异常收口为失败结果（携带原因，不向上抛）。</summary>
    public async Task<ScanRun> RunAsync()
    {
        try
        {
            // 第 1 步：快照采集（基础字段+采集型信号+失败记录）
            var snapshot = await _scanner.TakeSnapshot().ConfigureAwait(false);

            // 第 2 步：候选预筛（需验签的 PID 集）
            var candidates = _rules.CandidateIds(snapshot);

            // 第 3 步：仅对候选验签并回填（口径 #9“仅候选执行”）
            var verified = await _scanner.CollectSignatures(snapshot, candidates).ConfigureAwait(false);

            var classifications = await ClassifyCoreAsync(verified).ConfigureAwait(false);
            return ScanRun.Ok(new ScanOutcome(verified, classifications));
        }
        catch (Exception ex)
        {
            // PRD §3.7“扫描失败”行：错误提示“扫描失败：{原因}”由 VM 层映射；此处只收口原因。
            // 类型名前缀保底：部分 COM/WMI 包装异常 Message 为空串，仅消息会退化为无原因文案
            var reason = string.IsNullOrWhiteSpace(ex.Message)
                ? ex.GetType().Name
                : $"{ex.GetType().Name}: {ex.Message}";
            return ScanRun.Fail(reason);
        }
    }

    /// <summary>
    /// 对既有快照重跑判定（加白即时性，③.s4 裁决⑥）：白名单取提供者当前一致视图，
    /// 名单装载规则与扫描链同一实现（<see cref="ClassifyCoreAsync"/> 单点，兜底不分叉）。
    /// </summary>
    public Task<IReadOnlyList<Classification>> ReclassifyAsync(ScanResult verifiedSnapshot) =>
        ClassifyCoreAsync(verifiedSnapshot);

    /// <summary>判定步（名单包+白名单+上下文参数注入）。storage 法条：任一名单装载失败即整包替换为
    /// RulePack.Empty（禁残包入判定，system 法-3 经“保护性依据缺失”保守降级）。</summary>
    private async Task<IReadOnlyList<Classification>> ClassifyCoreAsync(ScanResult verified)
    {
        var packResult = _rulePackStore.LoadRulePack();
        var pack = packResult.Failures.Count > 0 ? RulePack.Empty : packResult.Pack;
        return await _rules.Classify(
            verified, _whitelistProvider(), pack, _context).ConfigureAwait(false);
    }
}
