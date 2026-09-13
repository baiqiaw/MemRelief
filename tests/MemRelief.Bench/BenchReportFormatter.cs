using System.Text;
using MemRelief.Core.Contracts;

namespace MemRelief.Bench;

/// <summary>
/// 验证台报告渲染（纯函数）：环境头/分段计时对照/分级统计/推荐全量明细/谨慎·受保护 Top N/失败摘要。
/// 输出风格对照 scripts/baseline-result.txt（编号段头 + 表列），供人工对照复核推荐准确性。
/// 判定依据行 = 口径编号 + 引擎 Detail 原文（依据完整可追溯，rules 法条），本类不做二次解读。
/// </summary>
public static class BenchReportFormatter
{
    public static string Format(BenchResult result, BenchEnvironment env, int topN)
    {
        var sb = new StringBuilder();
        var anomalies = result.TimingAnomalies();

        sb.AppendLine("=== MemRelief 真机扫描验证台（T-21） ===");
        sb.AppendLine(
            $"采样时间: {env.SampledAtLocal:yyyy-MM-dd HH:mm:ss} | 主机: {env.MachineName} | 引擎版本: {Core.ProductInfo.Version}");
        sb.AppendLine(
            $"快照进程总数: {result.Snapshot.ProcessCount} | 判定输出: {result.Classifications.Count} 条 | 计时异常: {(anomalies.Count == 0 ? "无" : string.Join("；", anomalies))}");
        sb.AppendLine();

        AppendTimings(sb, result);
        AppendLevelSummary(sb, result);
        AppendRecommendDetails(sb, result);
        AppendTopDetails(sb, result, topN);
        AppendFailureSummary(sb, result);
        return sb.ToString();
    }

    /// <summary>
    /// 多轮连扫的后续轮计时表（--repeat N 轮 2 起）：复用同一 Scanner 实例=签名缓存热态，
    /// 与 App 常驻复扫同构；明细区各轮高度重复，后续轮只出计时（进程数变化在头部行可见）。
    /// </summary>
    public static string FormatTimings(BenchResult result, BenchEnvironment env, int round)
    {
        var sb = new StringBuilder();
        var anomalies = result.TimingAnomalies();
        sb.AppendLine($"=== 第 {round} 轮（缓存热态，同 App 常驻复扫形态） ===");
        sb.AppendLine(
            $"采样时间: {env.SampledAtLocal:yyyy-MM-dd HH:mm:ss} | 快照进程总数: {result.Snapshot.ProcessCount}"
            + $" | 计时异常: {(anomalies.Count == 0 ? "无" : string.Join("；", anomalies))}");
        sb.AppendLine();
        AppendTimings(sb, result);
        return sb.ToString();
    }

    private static void AppendTimings(StringBuilder sb, BenchResult result)
    {
        sb.AppendLine("=== [1] 分段计时（预算对照 docs/specs/system-spec.md §7 分解） ===");
        AppendSection(sb, "采集快照", result.SnapshotMs, BenchBudgets.SnapshotMs);
        sb.AppendLine($"  候选预筛  {result.CandidateIdsMs,7} ms（已并入下行判定段对照值，无独立预算）");
        AppendSection(sb, "候选验签", result.VerifyMs, BenchBudgets.VerifyMs);
        AppendSection(sb, "判定", result.ClassifyMs + result.CandidateIdsMs, BenchBudgets.ClassifyMs,
            note: "（判定段含候选预筛与名单装载，口径同 App 侧编排方判定步）");
        AppendSection(sb, "引擎合计", result.EngineMs, BenchBudgets.EngineTotalMs);
        sb.AppendLine();
    }

    private static void AppendSection(StringBuilder sb, string name, long actualMs, long budgetMs, string? note = null)
    {
        var verdict = actualMs > budgetMs ? "超标" : "达标";
        sb.AppendLine($"  {name}  {actualMs,7} ms / 预算 {budgetMs} ms   {verdict}{note}");
    }

    private static void AppendLevelSummary(StringBuilder sb, BenchResult result)
    {
        var byLevel = result.Classifications
            .GroupBy(c => c.Level)
            .ToDictionary(g => g.Key, g => g.Count());

        long Count(Level level) => byLevel.GetValueOrDefault(level, 0);

        sb.AppendLine("=== [2] 分级统计 ===");
        sb.AppendLine(
            $"  ✅ 推荐 {Count(Level.Recommend)} | ⚠️ 谨慎 {Count(Level.Caution)} | 🚫 受保护 {Count(Level.Protected)}"
            + $" | 白名单 {Count(Level.Whitelisted)} | 未匹配 {Count(Level.Unmatched)}");
        sb.AppendLine();
    }

    private static void AppendRecommendDetails(StringBuilder sb, BenchResult result)
    {
        sb.AppendLine("=== [3] 推荐级明细（✅ 全量，供人工复核推荐准确性） ===");
        var recommends = result.Classifications
            .Where(c => c.Level == Level.Recommend)
            .ToList();
        if (recommends.Count == 0)
        {
            sb.AppendLine("  （无推荐级进程）");
            sb.AppendLine();
            return;
        }

        var snapshots = result.Snapshot.Snapshots.ToDictionary(p => p.Pid);
        foreach (var c in recommends)
        {
            snapshots.TryGetValue(c.Pid, out var p);
            var name = p?.Name ?? "(快照外)";
            var path = p?.ExecutablePath ?? "(路径不可得)";
            sb.AppendLine($"  PID {c.Pid,6} | 树 {Mb(c.TreePrivateBytes),8} MB | {name}");
            sb.AppendLine($"    路径: {path}");
            sb.AppendLine($"    依据: {DescribeBases(c)}");
            if (c.SourceEntries.Count > 0)
            {
                sb.AppendLine($"    来源: {string.Join("；", c.SourceEntries.Select(e => $"{e.Type}:{e.EntryName}"))}");
            }
        }
        sb.AppendLine();
    }

    private static void AppendTopDetails(StringBuilder sb, BenchResult result, int topN)
    {
        sb.AppendLine($"=== [4] 谨慎/受保护 Top {topN}（对照 scripts/baseline-result.txt 人工复核） ===");
        var rows = result.Classifications
            .Where(c => c.Level is Level.Caution or Level.Protected)
            .OrderByDescending(c => c.TreePrivateBytes)
            .ToList();
        var shown = rows.Take(topN).ToList();
        var snapshots = result.Snapshot.Snapshots.ToDictionary(p => p.Pid);

        foreach (var c in shown)
        {
            snapshots.TryGetValue(c.Pid, out var p);
            var name = p?.Name ?? "(快照外)";
            var mark = c.Level == Level.Protected ? "🚫" : "⚠️";
            sb.AppendLine($"  PID {c.Pid,6} | 树 {Mb(c.TreePrivateBytes),8} MB | {mark} {name}");
            sb.AppendLine($"    依据: {DescribeBases(c)}");
        }
        sb.AppendLine($"  （已显示 {shown.Count} / 共 {rows.Count} 行）");
        sb.AppendLine();
    }

    private static void AppendFailureSummary(StringBuilder sb, BenchResult result)
    {
        sb.AppendLine("=== [5] 采集失败记录摘要 ===");
        var failures = result.Snapshot.Failures;
        if (failures.Count == 0)
        {
            sb.AppendLine("  （无失败记录）");
            return;
        }

        var byKind = failures.GroupBy(f => f.Kind).OrderByDescending(g => g.Count());
        foreach (var g in byKind)
        {
            var global = g.Count(f => !f.Pid.HasValue);
            sb.AppendLine($"  {g.Key}: {g.Count()} 条（全局 {global} / 进程级 {g.Count() - global}）");
        }
        foreach (var f in failures.Take(5))
        {
            sb.AppendLine($"    例: SignalId={f.SignalId} pid={f.Pid?.ToString() ?? "全局"} {f.Detail}");
        }
        if (failures.Count > 5)
        {
            sb.AppendLine($"    （其余 {failures.Count - 5} 条略）");
        }
    }

    private static string DescribeBases(Classification c) =>
        c.Bases.Count == 0 ? "（无依据记录）" : string.Join("；", c.Bases.Select(b => $"#{b.SignalId} {b.Detail}"));

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1");
}
