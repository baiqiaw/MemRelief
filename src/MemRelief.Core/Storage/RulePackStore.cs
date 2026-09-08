using System.Text.Json;
using MemRelief.Core.Contracts;

namespace MemRelief.Core.Storage;

// 名单资源装载（storage 模块 T-13）。规格见 docs/specs/modules/storage.md §4.1；契约见 data-contracts.md §1.4。
// 法（storage §6）：保护类名单加载失败→上报失败，禁空名单放行——编排方按 Failures 非空传 RulePack.Empty，
//   使 rules 按"保护性依据缺失"保守兜底（法-3）；契约明示 v1 不区分真实空与失败，接受保守误降级。
// 法（storage §6）：本模块除白名单/日志外零文件写入——名单为只读嵌入资源，随单文件发布。

/// <summary>四份内置名单装载（storage.md §5 指名接口；消费者=App 编排装载）。</summary>
public interface IRulePackStore
{
    /// <summary>装载四份内置名单。任何一份失败均上报（fail-safe），不抛异常。</summary>
    RulePackLoadResult LoadRulePack();
}

public sealed class RulePackStore : IRulePackStore
{
    private const string ResourceRoot = "MemRelief.Core.Storage.Resources";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,   // 名单由用户手工迭代，宽容注释与尾逗号
        AllowTrailingCommas = true,
    };

    private readonly Func<string, string?> _resource;    // 名单名 → JSON 文本；null=缺失（构造注入便于测试）

    public RulePackStore(Func<string, string?>? resourceLoader = null)
    {
        _resource = resourceLoader ?? ReadEmbeddedResource;
    }

    public RulePackLoadResult LoadRulePack()
    {
        var failures = new List<RulePackLoadFailure>();
        var residual = LoadList(RulePackList.ResidualPatterns, "residual-patterns", ParseStrings, failures);
        var resident = LoadList(RulePackList.ResidentApps, "resident-apps", ParseStrings, failures);
        var security = LoadList(RulePackList.SecurityApps, "security-apps", ParseSecurityApps, failures);
        var sysProtected = LoadList(RulePackList.ProtectedProcesses, "protected-processes", ParseStrings, failures);
        return new RulePackLoadResult(
            new RulePack(residual, resident, security, sysProtected),
            failures);
    }

    private IReadOnlyList<T> LoadList<T>(
        RulePackList list,
        string name,
        Func<string, List<T>?> parse,
        List<RulePackLoadFailure> failures)
    {
        string? json;
        try { json = _resource(name); }
        catch (Exception ex) { failures.Add(new(list, $"名单资源读取失败：{ex.Message}")); return []; }

        if (json == null) { failures.Add(new(list, $"名单资源缺失：{name}")); return []; }

        List<T>? parsed;
        try { parsed = parse(json); }
        catch (Exception ex) { failures.Add(new(list, $"名单解析失败：{ex.Message}")); return []; }

        return parsed ?? [];
    }

    private static List<string> ParseStrings(string json) =>
        JsonSerializer.Deserialize<List<string>>(json, JsonOptions)?
            .Where(s => !string.IsNullOrWhiteSpace(s))       // 防御：空白条目（子串 "" 会命中一切进程）
            .Select(s => s.Trim())
            .ToList() ?? [];

    private static List<SecurityApp> ParseSecurityApps(string json) =>
        JsonSerializer.Deserialize<List<SecurityApp>>(json, JsonOptions)?
            .Where(a => a is not null)                       // 防御：null 元素（手工编辑失误）
            .Select(a => a with { Name = a.Name.Trim(), Signer = a.Signer?.Trim() })
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))  // 防御：精确名匹配（OrdinalIgnoreCase 不 Trim），带空格条目会静默漏配
            .ToList() ?? [];

    private static string? ReadEmbeddedResource(string name)
    {
        using var stream = typeof(RulePackStore).Assembly.GetManifestResourceStream($"{ResourceRoot}.{name}.json");
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
