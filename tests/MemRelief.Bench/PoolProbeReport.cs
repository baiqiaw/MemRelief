using System.Text;

namespace MemRelief.Bench;

/// <summary>
/// poolprobe 报告格式化：环境头（机器/采样时点/核数/实验参数）+ 结果表（含 PRD §3.4
/// 整批 ≤30s SLA 对照标记）。预算对照输出与 BenchBudgets 同定位（输出事实，不裁决）。
/// </summary>
public static class PoolProbeReport
{
    /// <summary>PRD §3.4 整批释放预算（多树并行整批 ≤30s）。</summary>
    public const long BatchSlaMs = 30_000;

    public static string Format(
        IReadOnlyList<PoolProbeResult> records, BenchEnvironment env, int waitMs, int pollMs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 释放链线程池承载实验（poolprobe，issue #40） ==");
        sb.AppendLine($"环境：{env.MachineName} | 采样时点：{env.SampledAtLocal:yyyy-MM-dd HH:mm:ss} | 逻辑核数：{Environment.ProcessorCount}");
        sb.AppendLine($"参数：单树等待 {waitMs}ms（轮询 {pollMs}ms） | 模型口径：sync=Task.Run+Thread.Sleep / async=Task.Delay（ProcessReleaser 等待段同构）");
        sb.AppendLine();
        sb.AppendLine("| 树数 | 模式 | 整批耗时(ms) | 线程数(前→后) | SLA ≤30s |");
        sb.AppendLine("|-----:|------|-------------:|--------------|----------|");
        foreach (var r in records)
        {
            var sla = r.TotalMs <= BatchSlaMs ? "✅ 达标" : "❌ 超标";
            sb.AppendLine($"| {r.Trees} | {r.Mode} | {r.TotalMs} | {r.ThreadsBefore}→{r.ThreadsAfter} | {sla} |");
        }

        return sb.ToString();
    }
}
