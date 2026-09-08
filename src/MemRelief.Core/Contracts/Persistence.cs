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

/// <summary>四份内置名单（对应 <see cref="RulePack"/> 四数组；基线内容见 PRD 附录名单清单）。</summary>
public enum RulePackList
{
    ResidualPatterns,
    ResidentApps,
    SecurityApps,
    ProtectedProcesses,
}

/// <summary>名单加载失败上报（List 定位名单，Reason 人读）。</summary>
public record RulePackLoadFailure(RulePackList List, string Reason);

/// <summary>装载结果：Pack 恒非 null（失败名单置空数组）；Failures 非空时编排方应传 RulePack.Empty 兜底。</summary>
public record RulePackLoadResult(RulePack Pack, IReadOnlyList<RulePackLoadFailure> Failures);
