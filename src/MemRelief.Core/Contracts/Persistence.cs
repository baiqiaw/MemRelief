namespace MemRelief.Core.Contracts;

// 持久化域契约（提供者 storage；rules/releaser 经编排方参数注入消费）。定义见 data-contracts.md §1.4

public record WhitelistEntry(string Name, DateTime AddedAtUtc, string? Path = null, string? Note = null);

/// <summary>一次扫描的白名单一致视图（名称匹配，v1 已知边界：同名不同路径一并排除）。</summary>
public record WhitelistSnapshot(IReadOnlyCollection<WhitelistEntry> Entries)
{
    public bool ContainsName(string name) => Entries.Any(e =>
        string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>内置名单包（内容基线见 PRD 附录）。</summary>
public record SecurityApp(string Name, string? Signer = null);

public record RulePack(
    IReadOnlyList<string> ResidualPatterns,
    IReadOnlyList<string> ResidentApps,
    IReadOnlyList<SecurityApp> SecurityApps,
    IReadOnlyList<string> ProtectedProcesses)
{
    /// <summary>名单加载失败时编排方传入的空包：任一名单为空即按"保护性依据缺失"兜底（system 法-3）。</summary>
    public static RulePack Empty { get; } = new(
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<SecurityApp>(), Array.Empty<string>());
}
