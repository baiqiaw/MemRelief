using MemRelief.Core.Contracts;

namespace MemRelief.Bench;

/// <summary>
/// 分段预算（docs/specs/system-spec.md §7 分解）：采集 ≤2.0s + 候选验签 ≤0.5s + 判定 ≤0.3s，
/// 引擎段合计 ≤2.8s。候选预筛（CandidateIds，判定域纯函数）无独立预算，耗时并入判定段计（报告注明口径）。
/// </summary>
public static class BenchBudgets
{
    public const long SnapshotMs = 2000;
    public const long VerifyMs = 500;
    public const long ClassifyMs = 300;
    public const long EngineTotalMs = 2800;
}

/// <summary>验证台运行环境头（采样时点与主机标识，报告溯源用）。</summary>
public sealed record BenchEnvironment(string MachineName, DateTime SampledAtLocal);

/// <summary>
/// 一次验证台运行的产物：验签回填后的快照 + 全量分类 + 四段计时。
/// 计时口径：判定段秒表覆盖名单装载与 Classify（编排方判定步口径，与 App 侧 ScanCoordinator.ClassifyCoreAsync
/// 同构，方向保守）；候选预筛对照值并入判定段（spec §7 无独立预算），引擎合计 = 四段全和（无遗漏）。
/// </summary>
public sealed record BenchResult(
    ScanResult Snapshot,
    IReadOnlyList<Classification> Classifications,
    long SnapshotMs,
    long CandidateIdsMs,
    long VerifyMs,
    long ClassifyMs,
    long TotalMs)
{
    /// <summary>引擎段合计（spec §7 ≤2.8s 验收口径）。</summary>
    public long EngineMs => SnapshotMs + CandidateIdsMs + VerifyMs + ClassifyMs;

    /// <summary>
    /// 计时异常清单（对抗性防御：秒表/时钟异常不静默输出假数据）——段耗时任一为负，
    /// 或总耗时小于引擎各段之和（时钟矛盾）。空清单 = 计时可信。
    /// </summary>
    public IReadOnlyList<string> TimingAnomalies()
    {
        var issues = new List<string>();
        if (SnapshotMs < 0)
        {
            issues.Add($"采集段耗时为负（{SnapshotMs}ms）");
        }
        if (CandidateIdsMs < 0)
        {
            issues.Add($"候选预筛耗时为负（{CandidateIdsMs}ms）");
        }
        if (VerifyMs < 0)
        {
            issues.Add($"验签段耗时为负（{VerifyMs}ms）");
        }
        if (ClassifyMs < 0)
        {
            issues.Add($"判定段耗时为负（{ClassifyMs}ms）");
        }
        if (TotalMs < 0)
        {
            issues.Add($"总耗时为负（{TotalMs}ms）");
        }
        else if (EngineMs > TotalMs)
        {
            issues.Add($"引擎合计（{EngineMs}ms）超过总耗时（{TotalMs}ms），时钟矛盾");
        }
        return issues;
    }
}
