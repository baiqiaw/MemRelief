using MemRelief.Core.Contracts;

namespace MemRelief.App.Text;

/// <summary>
/// 依据→中文文案映射与空值口径的唯一承载（ui.md §6 法条：映射表集中在 App 侧一处）。
/// 承载：原因说明拼接（Detail 自解释直展，issue #28 裁决②；SignalId=8 失败恢复服务按 R02 GWT 补全
/// services.msc 建议后缀）、拉起提示三态（按来源类型分流建议工具）、四类来源中文名、
/// 空值口径（孤儿/无来源/未评估）、树合计格式、查询结果与组标题。禁在别处内联第二份映射。
/// </summary>
internal static class DisplayText
{
    /// <summary>PRD F2 空值口径：孤儿项主进程名后缀（R02 GWT 字面半角括号）。</summary>
    public const string OrphanSuffix = "(父进程已退出)";

    /// <summary>PRD F2 空值口径：无四类来源时的来源文案。</summary>
    public const string NoSource = "无（手动启动）";

    /// <summary>原因说明：逐条依据按行拼接（顺序即 Classify 产出序，冲突消解依据可追溯）。</summary>
    public static string Reason(IReadOnlyList<Basis> bases) =>
        bases.Count == 0 ? "未命中任何规则" : string.Join("\n", bases.Select(ReasonOne));

    /// <summary>
    /// 拉起提示三态（R02 六要素之六；WouldBeRevived null=不适用/未评估）。
    /// true 态按来源类型分流禁用入口建议：服务→services.msc（PRD R02 GWT）、计划任务→taskschd.msc（PRD §2 来源体检）、
    /// 其余来源仅提示会被拉起（防对计划任务项误指 services.msc）。
    /// </summary>
    public static string ReviveHint(bool? wouldBeRevived, IReadOnlyList<SourceEntry> sourceEntries) =>
        wouldBeRevived switch
        {
            true => "杀掉后会被重新拉起" + DisableHint(sourceEntries),
            false => "杀掉后不会被拉起",
            null => "是否会被拉起：未评估",
        };

    private static string DisableHint(IReadOnlyList<SourceEntry> entries)
    {
        if (entries.Any(e => e.Type == SourceType.Service))
        {
            return "，建议先禁用来源（services.msc）";
        }

        return entries.Any(e => e.Type == SourceType.ScheduledTask)
            ? "，建议先禁用来源（taskschd.msc）"
            : string.Empty;
    }

    /// <summary>来源文案：“类型：条目名”分号拼接；无来源显示“无（手动启动）”。</summary>
    public static string Source(IReadOnlyList<SourceEntry> entries) =>
        entries.Count == 0
            ? NoSource
            : string.Join("；", entries.Select(e => $"{SourceTypeName(e.Type)}：{e.EntryName}"));

    /// <summary>树合计内存（口径 #13），MB 一位小数。</summary>
    public static string TreeMb(long bytes) => $"{bytes / 1024.0 / 1024:F1} MB";

    /// <summary>搜索结果判定文案（R02 GWT：不可见原因=未命中规则/白名单排除）。</summary>
    public static string QueryOutcome(Level outcome) => outcome switch
    {
        Level.Recommend => "推荐级（在列表中）",
        Level.Caution => "谨慎级（在列表中）",
        Level.Protected => "不推荐级（在列表中）",
        Level.Whitelisted => "白名单排除",
        _ => "未命中规则，不进列表",
    };

    /// <summary>组标题（带项数计数；🚫 折叠态靠此承载“仅计数”）。</summary>
    public static string GroupTitle(Level level, int count) => level switch
    {
        Level.Recommend => $"✅ 推荐可释放（{count} 项）",
        Level.Caution => $"⚠️ 谨慎（{count} 项）",
        Level.Protected => $"🚫 不推荐（{count} 项）",
        _ => level.ToString(),
    };

    /// <summary>
    /// 单条依据展示文案：Detail 自解释直展（#28 裁决②）；唯一补全=口径 #8 失败恢复服务，
    /// 按 R02 GWT 附加 services.msc 禁用来源建议（服务名参数在 Detail 内，此处不反解析）；
    /// Detail 为空（引擎违约）按占位收口不 NRE。
    /// </summary>
    private static string ReasonOne(Basis basis)
    {
        var detail = string.IsNullOrWhiteSpace(basis.Detail) ? "（依据描述缺失）" : basis.Detail;
        return basis.SignalId == 8 && detail.StartsWith("杀掉后会被服务管理器拉起", StringComparison.Ordinal)
            ? $"{detail}，建议先禁用来源（services.msc）"
            : detail;
    }

    private static string SourceTypeName(SourceType type) => type switch
    {
        SourceType.RunKey => "注册表自启动",
        SourceType.Service => "Windows 服务",
        SourceType.ScheduledTask => "计划任务",
        SourceType.StartupFolder => "启动文件夹",
        _ => type.ToString(),
    };
}
