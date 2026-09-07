namespace MemRelief.Core.Contracts;

// 判定域契约（提供者 rules）。定义见 docs/specs/interfaces/data-contracts.md §1.2

/// <summary>三级判定级别。Unmatched=0：default/解析失败落到最保守值（不进列表），不落到可勾选级。</summary>
public enum Level
{
    Unmatched,
    Whitelisted,
    Recommend,
    Caution,
    Protected,
}

/// <summary>
/// 判定依据：一条命中规则的机器可读记录。SignalId = PRD F1 口径表编号（#1–#15）；
/// 0 = 非口径表依据的保留值（采集失败兜底、本工具自身、系统保护穷举名、名单不可用等，
/// 口径表无对应行），Detail 自解释。原因文案由 ui 映射（system §6 情报-1）。
/// </summary>
public record Basis(int SignalId, string Detail);

public record Classification(
    int Pid,
    Level Level,
    IReadOnlyList<Basis> Bases,
    long TreePrivateBytes,
    bool? WouldBeRevived,
    IReadOnlyList<SourceEntry> SourceEntries,
    bool RequiresElevation)
{
    public IReadOnlyList<Basis> Bases { get; init; } = Bases ?? Array.Empty<Basis>();
    public IReadOnlyList<SourceEntry> SourceEntries { get; init; } =
        SourceEntries == null ? Array.Empty<SourceEntry>() : SourceEntries.ToArray();
}

/// <summary>编排方构造的判定上下文：纯函数约束下环境信息一律参数注入（契约 2026-09-05 修订）。</summary>
public record ClassificationContext(int SelfPid, string? CurrentUserName);

/// <summary>
/// 判定查询结果（R02 搜索框）。Target=(Pid,Name)；Outcome 复用 Level：
/// Unmatched=未命中规则不进列表、Whitelisted=白名单排除；Bases 与对应 Classification 一致。
/// </summary>
public record QueryResult(
    int Pid,
    string Name,
    Level Outcome,
    IReadOnlyList<Basis> Bases)
{
    public IReadOnlyList<Basis> Bases { get; init; } = Bases ?? Array.Empty<Basis>();
}
