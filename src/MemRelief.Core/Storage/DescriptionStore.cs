using System.Text.Json;
using MemRelief.Core.Contracts;

namespace MemRelief.Core.Storage;

/// <summary>
/// 进程说明手册装载与解析（#58）：嵌入资源 process-descriptions.json → <see cref="ProcessDescriptionEntry"/> 表；
/// fail-safe 语义（缺失/损坏/读取异常 → 空表，仅说明退化，不影响任何判定——与 RulePackStore 保护性降级刻意分立）。
/// 说明取值优先级纯函数同置本类：<see cref="Resolve"/>（手册命中 &gt; exe FileDescription &gt; CompanyName &gt; 无）。
/// </summary>
public sealed class DescriptionStore : IDescriptionStore
{
    private const string ResourceRoot = "MemRelief.Core.Storage.Resources";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,   // 手册由用户手工迭代，宽容注释与尾逗号（同名单口径）
        AllowTrailingCommas = true,
    };

    private readonly Func<string, string?> _resource;    // 资源名 → JSON 文本；null=缺失（构造注入便于测试）

    public DescriptionStore(Func<string, string?>? resourceLoader = null)
    {
        _resource = resourceLoader ?? ReadEmbeddedResource;
    }

    public IReadOnlyList<ProcessDescriptionEntry> Load()
    {
        string? json;
        try { json = _resource("process-descriptions"); }
        catch (Exception) { return []; }   // fail-safe：读取异常仅说明退化（#58，非保护性数据面）

        if (json == null) return [];

        try
        {
            return JsonSerializer.Deserialize<List<ProcessDescriptionEntry>>(json, JsonOptions)?
                .Where(e => e is not null)
                // null 字段归一为空串落入下方逐条过滤（#58 评审修复：防坏条目击穿整本手册）
                .Select(e => e with { Match = e.Match?.Trim() ?? "", Description = e.Description?.Trim() ?? "" })
                .Where(e => !string.IsNullOrWhiteSpace(e.Match) && !string.IsNullOrWhiteSpace(e.Description))
                .ToList() ?? [];
        }
        catch (Exception)
        {
            return [];         // fail-safe：解析失败仅说明退化
        }
    }

    /// <summary>说明取值优先级（#58）：手册命中（名/路径子串，OrdinalIgnoreCase）&gt; FileDescription &gt; CompanyName &gt; null。</summary>
    public static string? Resolve(
        string? name,
        string? path,
        string? fileDescription,
        string? companyName,
        IReadOnlyList<ProcessDescriptionEntry> entries)
    {
        foreach (var entry in entries)
        {
            if ((name is not null && name.Contains(entry.Match, StringComparison.OrdinalIgnoreCase))
                || (path is not null && path.Contains(entry.Match, StringComparison.OrdinalIgnoreCase)))
            {
                return entry.Description;
            }
        }

        return NullOrWhiteSpace(fileDescription) ?? NullOrWhiteSpace(companyName);
    }

    private static string? NullOrWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadEmbeddedResource(string name)
    {
        using var stream = typeof(DescriptionStore).Assembly.GetManifestResourceStream($"{ResourceRoot}.{name}.json");
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
