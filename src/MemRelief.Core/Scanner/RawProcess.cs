namespace MemRelief.Core.Scanner;

/// <summary>进程句柄打开结果（data-contracts §2 T-01 裁决③④：被拒→AccessDenied；消失→Unreadable）。</summary>
public enum ProcessOpenOutcome
{
    Opened,

    /// <summary>OpenProcess 被拒（PPL 等）。</summary>
    AccessDenied,

    /// <summary>进程在扫描中途退出，pid 已失效。</summary>
    Vanished,
}

/// <summary>单字段采集结果：IsOk=true 取 Value；否则 Error 为人读原因（落 SignalFailure.Detail）。</summary>
public readonly record struct ProcessField<T>(T? Value, string? Error)
{
    public bool IsOk => Error is null;

    public static ProcessField<T> Ok(T value) => new(value, null);

    public static ProcessField<T> Fail(string error) => new(default, error);
}

/// <summary>
/// 采集侧原始行：Toolhelp 枚举行 + 句柄级字段结果（字段名与契约 ProcessSnapshot 对齐）。
/// 仅数据承载，无逻辑；判定/映射归 <see cref="SnapshotAssembler"/>（纯函数）。
/// </summary>
/// <param name="CommandLine">契约 v1：通道级失败仅字段级 null 表达、无 SignalFailure（data-contracts §1.1）。</param>
public sealed record RawProcess(
    int Pid,
    int ParentPid,
    string Name,
    ProcessOpenOutcome Open,
    ProcessField<string?> ExecutablePath,
    ProcessField<DateTime?> CreationTimeUtc,
    ProcessField<long?> PrivateCommittedBytes,
    ProcessField<string?> OwnerUser,
    string? CommandLine);
