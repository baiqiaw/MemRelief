namespace MemRelief.App.Text;

/// <summary>
/// 内存概览条文案与格式映射（R05/T-17，映射集中在 App 文案层）：三数值 GB 换算、占用率、
/// 口径说明（PRD F5）、降级注记、读失败提示（PRD §3.7“内存信息读取失败”行）。
/// </summary>
public static class OverviewText
{
    /// <summary>无数据占位（读失败与降级备用位共用；降级仅备用位、读失败三数值全占位）。</summary>
    public const string Placeholder = "—";

    /// <summary>口径说明（PRD F5 处理逻辑原文承载）：占用率仅统计使用中、备用属可回收缓存、
    /// 高占用未必是内存压力。</summary>
    public const string CaliberNote =
        "口径说明：占用率仅统计“使用中”内存；备用（standby）属可回收缓存，不计入占用率。" +
        "高占用且无卡顿、提交余量充足时未必是内存压力。";

    /// <summary>降级注记（PRD F5：standby 不可得时降级为两数值并注记；
    /// 降级原因通道级不可观测系已知边界，issue #31，文案不虚构原因）。</summary>
    public const string DegradedNote = "数据源降级：备用缓存数值暂不可用，已按两数值呈现。" + CaliberNote;

    /// <summary>读失败提示（PRD §3.7 文案“内存数据暂不可用”+ 不阻塞说明与自动重试告知）。</summary>
    public const string ReadFailedNote = "内存数据暂不可用（读取失败，不影响扫描与释放；下次刷新自动重试）";

    /// <summary>字节 → GB 一位小数（与资源监视器同量级对照，R05 GWT）。</summary>
    public static string Gb(long bytes) => (bytes / 1024.0 / 1024 / 1024).ToString("F1") + " GB";

    /// <summary>占用率 = InUse / 物理总量，四舍五入（AwayFromZero，与用户直觉一致——银行家舍入会让
    /// 中点值 12.5% 显示 12%）；总量非正（防御夹具/异常计数器）按 0%，不产生 NaN。</summary>
    public static string Percent(long inUseBytes, long physicalTotalBytes)
        => physicalTotalBytes > 0
            ? $"{Math.Round(inUseBytes * 100.0 / physicalTotalBytes, MidpointRounding.AwayFromZero)}%"
            : "0%";
}
