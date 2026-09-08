using MemRelief.Core.Contracts;

namespace MemRelief.Core.Scanner;

/// <summary>
/// 内存概览采样（R05 数据侧，scanner.md §4.1"采集概览"）：三级通道梯——
/// ①NtQuerySystemInformation（含 standby）；②PDH 计数器（commit+standby）；③GlobalMemoryStatusEx 终底
/// （commit 用页面文件口径近似，Source=Degraded）。降级语义（AC）：standby 不可得 ⇔ StandbyBytes=null 且
/// Source=Degraded，三通道统一由本类纯函数收口（单测不变式覆盖）；GlobalMemoryStatusEx 锚点失败走显式异常
/// （GlobalMemoryChannel），编排方按采样失败承接。
/// "与资源监视器同分钟对照 ≤10%"（R05 GWT）归 T-22 真机验收，自动化同刻对照代理见 ScannerIntegrationTests。
/// </summary>
public sealed class MemoryOverviewSampler
{
    /// <summary>按通道梯采样一次概览。通道降级不抛异常；终底锚点失败显式上抛（GlobalMemoryChannel 契约）。</summary>
    public MemoryOverview Sample()
    {
        var gms = GlobalMemoryChannel.QuerySnapshot();
        if (NtMemoryChannel.TryQueryPerformance(out var availablePages, out var committedPages, out var commitLimitPages))
        {
            var standbyOk = NtMemoryChannel.TryQueryStandbyList(out var standbyByPriority);
            return FromNtQuery(gms.TotalPhysBytes, availablePages, committedPages, commitLimitPages,
                (uint)Environment.SystemPageSize, standbyOk ? standbyByPriority : null);
        }
        if (PdhMemoryChannel.TryQuery(out var committedBytes, out var commitLimitBytes, out var availableBytes, out var standbyBytes))
        {
            return FromPdh(committedBytes, commitLimitBytes, availableBytes, standbyBytes, gms.TotalPhysBytes);
        }
        return FromGlobalMemory(gms.TotalPhysBytes, gms.AvailPhysBytes, gms.TotalPageFileBytes, gms.AvailPageFileBytes);
    }

    /// <summary>NtQuery 通道换算：页计数×页大小；standby 缺失或全零 → 降级（v1 不区分真实空与不可得）。</summary>
    internal static MemoryOverview FromNtQuery(
        long physicalTotalBytes, uint availablePages, uint committedPages, uint commitLimitPages,
        uint pageSize, ulong[]? standbyByPriority)
    {
        long? standby = null;
        if (standbyByPriority is { Length: 8 } && standbyByPriority.Any(s => s > 0))
        {
            // 溢出防御：求和逐项钳到 long 语义内，页数×页大小上限钳到 long.MaxValue
            var sumPages = standbyByPriority.Aggregate(0UL, (acc, s) => checked(acc + Math.Min(s, (ulong)long.MaxValue - acc)));
            var pageSizeSafe = Math.Max(pageSize, 1UL);
            standby = (long)(Math.Min(sumPages, (ulong)(long.MaxValue / pageSizeSafe)) * pageSizeSafe);
        }
        return new MemoryOverview(
            physicalTotalBytes,
            ClampInUse(physicalTotalBytes, checked((long)availablePages * pageSize)),
            ClampToLong((double)committedPages * pageSize),
            ClampToLong((double)commitLimitPages * pageSize),
            standby,
            standby is null ? MemoryOverviewSource.Degraded : MemoryOverviewSource.NtQuery);
    }

    /// <summary>PDH 通道换算：计数器字节值直读（InUse 口径 = PDH Available Bytes）；standby 缺失或合计 ≤0 → 降级。</summary>
    internal static MemoryOverview FromPdh(
        double committedBytes, double commitLimitBytes, double availableBytes, double? standbyBytes,
        long physicalTotalBytes)
    {
        return new MemoryOverview(
            physicalTotalBytes,
            ClampInUse(physicalTotalBytes, ClampToLong(availableBytes)),
            ClampToLong(committedBytes),
            ClampToLong(commitLimitBytes),
            standbyBytes is > 0 ? ClampToLong(standbyBytes.Value) : null,
            standbyBytes is > 0 ? MemoryOverviewSource.Pdh : MemoryOverviewSource.Degraded);
    }

    /// <summary>终底换算：GlobalMemoryStatusEx（commit 用页面文件口径近似，Source=Degraded，standby 恒 null）。</summary>
    internal static MemoryOverview FromGlobalMemory(
        long totalPhysBytes, long availPhysBytes, long totalPageFileBytes, long availPageFileBytes)
    {
        return new MemoryOverview(
            totalPhysBytes,
            ClampInUse(totalPhysBytes, availPhysBytes),
            Math.Max(0, totalPageFileBytes - Math.Min(totalPageFileBytes, Math.Max(0, availPageFileBytes))),
            totalPageFileBytes,
            null,
            MemoryOverviewSource.Degraded);
    }

    /// <summary>可用 > 物理总量（计数器毛刺）→ 0，禁负值外泄给 ui/释放量计算。</summary>
    private static long ClampInUse(long physicalTotalBytes, long availableBytes)
        => Math.Max(0, physicalTotalBytes - Math.Min(physicalTotalBytes, Math.Max(0, availableBytes)));

    /// <summary>double 计数器值 → long：负值/NaN 钳 0，超界钳 long.MaxValue（直接转换超界会得未定义负值）。</summary>
    private static long ClampToLong(double value)
    {
        if (double.IsNaN(value) || value <= 0)
        {
            return 0;
        }
        // 9.0e18 < long.MaxValue（≈9.223e18），阈值下转换安全
        return value >= 9.0e18 ? long.MaxValue : (long)value;
    }
}
