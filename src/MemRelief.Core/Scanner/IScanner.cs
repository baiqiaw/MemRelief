using MemRelief.Core.Contracts;

namespace MemRelief.Core.Scanner;

/// <summary>
/// 扫描域对外接口（scanner.md §5）：提供 TakeSnapshot/CollectSignatures/SampleOverview；消费者=App（编排）。
/// </summary>
public interface IScanner
{
    /// <summary>采集快照（口径#1/#13 数据侧 + 基础字段 + 失败记录框架）。</summary>
    Task<ScanResult> TakeSnapshot();

    /// <summary>候选验签（两阶段协议采集侧，口径#9 仅候选执行；T-04 实装，issue #11）。</summary>
    Task<ScanResult> CollectSignatures(ScanResult snapshot, ISet<int> candidatePids);

    /// <summary>内存概览三数值采样（NtQuery 优先/PDH 兜底；T-05 实装，issue #9）。</summary>
    Task<MemoryOverview> SampleOverview();
}
