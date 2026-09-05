namespace MemRelief.Core.Contracts;

// 扫描域契约（提供者 scanner；rules/releaser/ui 消费）。定义见 docs/specs/interfaces/data-contracts.md §1.1

public enum OrphanHint
{
    No,
    ParentDead,
    PidReused,
}

public enum SignatureStatus
{
    NotCollected,
    Unverifiable,
    Microsoft,
    ValidNonMicrosoft,
    Invalid,
    Unsigned,
}

public enum SourceType
{
    RunKey,
    Service,
    ScheduledTask,
    StartupFolder,
}

public record SourceEntry(SourceType Type, string EntryName);

/// <summary>口径 #3：同可执行目录下的其他存活进程（路径归一化在采集侧完成，WBS T-03）。</summary>
public record SignalSet(
    OrphanHint OrphanHint = OrphanHint.No,
    IReadOnlySet<int>? SameDirAlivePids = null,
    bool? HasVisibleWindow = null,
    bool IsUwpPackage = false,
    int TcpEstablishedCount = 0,
    double? CpuDeltaSeconds = null,
    string? ServiceName = null,
    bool? ServiceRestartOnFailure = null,
    SignatureStatus SignatureStatus = SignatureStatus.NotCollected,
    string? SignerName = null,
    bool? IsSystemDirectory = null,
    IReadOnlyList<SourceEntry>? SourceEntries = null,
    bool? ScheduledTaskWouldRevive = null)
{
    public IReadOnlySet<int> SameDirAlivePids { get; init; } =
        SameDirAlivePids == null ? new HashSet<int>() : new HashSet<int>(SameDirAlivePids);
    public IReadOnlyList<SourceEntry> SourceEntries { get; init; } =
        SourceEntries == null ? Array.Empty<SourceEntry>() : SourceEntries.ToArray();
}

public enum FailureKind
{
    /// <summary>打开受拒（PPL 等）→ 🚫 受保护。</summary>
    AccessDenied,

    /// <summary>部分元数据不可读（非 PPL）→ ⚠️ 元数据不可读，不进✅。</summary>
    Unreadable,

    /// <summary>采集器/通道失败 → 相关进程不进✅。</summary>
    CollectorFailed,
}

/// <summary>采集失败记录。Pid=null 表示采集器级全局失败；保守兜底按 Kind+Pid 精确承载（system 法-3）。</summary>
public record SignalFailure(int SignalId, int? Pid, FailureKind Kind, string Detail);

public record ProcessSnapshot(
    int Pid,
    int ParentPid,
    string Name,
    string? ExecutablePath,
    DateTime CreationTimeUtc,
    long PrivateCommittedBytes,
    string? CommandLine = null,
    string? OwnerUser = null,
    SignalSet? Signals = null)
{
    public SignalSet Signals { get; init; } = Signals ?? new SignalSet();
}

/// <summary>一次扫描的一致视图，不可变。</summary>
public record ScanResult(
    DateTime TakenAtUtc,
    int ProcessCount,
    long DurationMs,
    IReadOnlyList<ProcessSnapshot> Snapshots,
    IReadOnlyList<SignalFailure> Failures)
{
    public IReadOnlyList<ProcessSnapshot> Snapshots { get; init; } = Snapshots ?? Array.Empty<ProcessSnapshot>();
    public IReadOnlyList<SignalFailure> Failures { get; init; } = Failures ?? Array.Empty<SignalFailure>();
}
