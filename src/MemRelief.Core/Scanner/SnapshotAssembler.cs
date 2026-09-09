using MemRelief.Core.Contracts;

namespace MemRelief.Core.Scanner;

/// <summary>
/// 快照装配（纯函数）：原始行 → ScanResult。
/// 字段映射、失败→SignalFailure、OrphanHint 解析（口径#1 数据侧）、Pid 唯一键校验均在此；
/// 零 I/O 零状态，同输入同输出。裁决依据 data-contracts §2 T-01 裁决记。
/// </summary>
public static class SnapshotAssembler
{
    /// <summary>创建时间不可读哨兵（裁决②）：值同 MinValue，Kind 强制 Utc（契约：Kind 须为 Utc）。</summary>
    internal static readonly DateTime UnreadableCreation = new(DateTime.MinValue.Ticks, DateTimeKind.Utc);

    public static ScanResult Assemble(IReadOnlyList<RawProcess> rows, DateTime takenAtUtc, long durationMs) =>
        Assemble(rows, signals: null, takenAtUtc, durationMs);

    /// <summary>T-02 扩展：带活动信号输入的装配（SignalInputs null = 信号未采集，字段保持契约默认/null 语义）。</summary>
    public static ScanResult Assemble(IReadOnlyList<RawProcess> rows, SignalInputs? signals, DateTime takenAtUtc, long durationMs)
    {
        var snapshots = new List<ProcessSnapshot>(rows.Count);
        var failures = new List<SignalFailure>();

        // 全局通道失败整次扫描一条（扫描级事实，与 per-pid 失败分离；契约 §1.1）
        if (signals is not null)
        {
            AddGlobalSignalFailures(signals, failures);
        }

        foreach (var row in rows)
        {
            var snapshot = ToSnapshot(row);
            CollectFailures(row, failures);
            if (signals is not null)
            {
                snapshot = snapshot with { Signals = MergeSignals(row, signals, failures) };
            }
            snapshots.Add(snapshot);
        }

        // Pid 唯一键（契约 §1.1：违约定=扫描失败，异常由编排方转 ScanFailed）
        if (snapshots.Select(s => s.Pid).Distinct().Count() != snapshots.Count)
        {
            throw new InvalidOperationException("Pid 唯一键违约定：快照内出现重复 Pid，本次扫描失败");
        }

        ResolveOrphanHints(snapshots);

        return new ScanResult(
            EnsureUtc(takenAtUtc),
            snapshots.Count,
            durationMs,
            snapshots.ToArray(),
            failures.ToArray());
    }

    /// <summary>通道级全局失败登记（Pid=null=全量进程不进✅ 的全有全无语义，契约 §1.1；每口径至多一条）。</summary>
    private static void AddGlobalSignalFailures(SignalInputs inputs, List<SignalFailure> failures)
    {
        if (inputs.WindowEnumerationFailed)
        {
            failures.Add(new SignalFailure(4, null, FailureKind.CollectorFailed, "窗口枚举失败（EnumWindows 通道）"));
        }
        if (inputs.TcpTableFailed)
        {
            failures.Add(new SignalFailure(6, null, FailureKind.CollectorFailed, "TCP 连接表读取失败（GetExtendedTcpTable）"));
        }
        if (inputs.ServiceEnumerationFailed)
        {
            failures.Add(new SignalFailure(8, null, FailureKind.CollectorFailed, "服务枚举失败（OpenSCManager/EnumServicesStatusEx）"));
        }
        if (inputs.DirectoryResolveFailed)
        {
            failures.Add(new SignalFailure(10, null, FailureKind.CollectorFailed, "系统目录清单解析失败（%windir%/Known Folder）"));
        }
    }

    /// <summary>
    /// 单行活动信号合并（T-02，五口径 → SignalSet；纯函数，仅 per-pid 语义——全局失败由 Assemble 统一登记）。
    /// 配对不变量：per-pid 采集失败（CPU/服务恢复配置不可读）登记对应口径 SignalFailure——
    /// rules 以 Failures 为保守兜底事实源，字段默认值不构成"已核实"（data-contracts §1.1）。
    /// </summary>
    public static SignalSet MergeSignals(RawProcess row, SignalInputs inputs, List<SignalFailure> failures)
    {
        var path = row.ExecutablePath.IsOk ? row.ExecutablePath.Value : null;

        // 口径 #4 可见窗口：VisiblePids 缺席=false；全局枚举失败由 Assemble 登记
        bool? hasVisibleWindow = inputs.WindowEnumerationFailed
            ? null
            : inputs.VisiblePids.Contains(row.Pid);

        // 口径 #6 活跃 TCP：缺席=0（表失败由 Assemble 登记）
        var tcp = inputs.TcpEstablished.TryGetValue(row.Pid, out var established) ? established : 0;

        // 口径 #7 CPU 差分：null（窗口内退出/采样失败/缺键）→ per-pid failure（口径兜底"不可读→不进✅"）
        var cpu = inputs.CpuDeltas.TryGetValue(row.Pid, out var delta) ? delta : null;
        if (cpu is null)
        {
            failures.Add(new SignalFailure(7, row.Pid, FailureKind.Unreadable, "CPU 差分不可得（窗口内退出或采样失败）"));
        }

        // 口径 #8 服务关联：未命中=非服务；QueryServiceConfig2 读取失败（RestartOnFailure=null，含多服务归并 null 传播）→ per-pid failure
        string? serviceName = null;
        bool? restartOnFailure = null;
        if (inputs.Services.TryGetValue(row.Pid, out var service))
        {
            serviceName = service.Name;
            restartOnFailure = service.RestartOnFailure;
            if (restartOnFailure is null)
            {
                failures.Add(new SignalFailure(8, row.Pid, FailureKind.Unreadable, $"服务失败恢复配置不可读：{service.Name}"));
            }
        }

        // 口径 #10 系统目录 + #4 UWP 特例：路径不可读 → null（编号 100 已覆盖，不重复 #10）；解析失败由 Assemble 登记
        bool? isSystemDirectory;
        var isUwp = false;
        if (path is null)
        {
            isSystemDirectory = null;
        }
        else if (inputs.DirectoryResolveFailed)
        {
            isSystemDirectory = null;
        }
        else
        {
            isSystemDirectory = SignalRules.IsUnderAnyPrefix(path, inputs.SystemDirectoryPrefixes);
            isUwp = inputs.UwpPackagePrefix is not null && SignalRules.PathMatchesPrefix(path, inputs.UwpPackagePrefix);
        }

        return new SignalSet
        {
            HasVisibleWindow = hasVisibleWindow,
            IsUwpPackage = isUwp,
            TcpEstablishedCount = tcp,
            CpuDeltaSeconds = cpu,
            ServiceName = serviceName,
            ServiceRestartOnFailure = restartOnFailure,
            IsSystemDirectory = isSystemDirectory,
        };
    }

    private static ProcessSnapshot ToSnapshot(RawProcess row)
    {
        // 创建时间：成功值强制 Utc；不可读取 MinValue 哨兵（裁决②）
        var creation = row.CreationTimeUtc.IsOk && row.CreationTimeUtc.Value is { } c
            ? EnsureUtc(c)
            : UnreadableCreation;

        // 私有提交：不可读按口径#13 兜底记 0（裁决③）
        var commit = row.PrivateCommittedBytes.IsOk && row.PrivateCommittedBytes.Value is { } v ? v : 0;

        return new ProcessSnapshot(
            row.Pid,
            row.ParentPid,
            row.Name,
            row.ExecutablePath.IsOk ? row.ExecutablePath.Value : null,
            creation,
            commit,
            row.CommandLine,
            row.OwnerUser.IsOk ? row.OwnerUser.Value : null);
    }

    private static void CollectFailures(RawProcess row, List<SignalFailure> failures)
    {
        switch (row.Open)
        {
            case ProcessOpenOutcome.AccessDenied:
                // 进程级打开失败单条覆盖四基础字段，不逐字段重复（裁决③）
                failures.Add(new SignalFailure(ScannerSignalIds.ProcessOpen, row.Pid, FailureKind.AccessDenied, "打开进程被拒（可能为受保护进程/PPL）"));
                return;
            case ProcessOpenOutcome.Vanished:
                // 中性措辞：涵盖扫描中退出与 pid 0 等恒不可打开的系统进程，不作无法证实的叙事
                failures.Add(new SignalFailure(ScannerSignalIds.ProcessOpen, row.Pid, FailureKind.Unreadable, "pid 无法打开（已退出或不可访问），元数据不可读"));
                return;
        }

        // 打开成功：逐字段失败各落一条编号段记录（裁决③）
        if (!row.ExecutablePath.IsOk)
        {
            failures.Add(new SignalFailure(ScannerSignalIds.ExecutablePath, row.Pid, FailureKind.Unreadable, $"可执行路径不可读：{row.ExecutablePath.Error}"));
        }
        if (!row.CreationTimeUtc.IsOk)
        {
            failures.Add(new SignalFailure(ScannerSignalIds.CreationTimeUtc, row.Pid, FailureKind.Unreadable, $"创建时间不可读：{row.CreationTimeUtc.Error}"));
        }
        if (!row.PrivateCommittedBytes.IsOk)
        {
            failures.Add(new SignalFailure(ScannerSignalIds.PrivateCommittedBytes, row.Pid, FailureKind.Unreadable, $"私有提交不可读（按口径#13 兜底记 0）：{row.PrivateCommittedBytes.Error}"));
        }
        if (!row.OwnerUser.IsOk)
        {
            failures.Add(new SignalFailure(ScannerSignalIds.OwnerUser, row.Pid, FailureKind.Unreadable, $"所有者不可读：{row.OwnerUser.Error}"));
        }
        // CommandLine 无失败记录通道（契约 §1.1 v1：仅字段级 null 表达）
    }

    /// <summary>口径#1 数据侧：ParentDead/PidReused 解析（PRD：父 pid 无对应进程→孤儿；父创建时间晚于本进程→PID 复用孤儿）。</summary>
    private static void ResolveOrphanHints(List<ProcessSnapshot> snapshots)
    {
        var byPid = new Dictionary<int, ProcessSnapshot>(snapshots.Count);
        foreach (var s in snapshots)
        {
            byPid[s.Pid] = s;
        }

        for (var i = 0; i < snapshots.Count; i++)
        {
            var s = snapshots[i];
            var hint = OrphanHint.No;

            if (s.ParentPid != s.Pid) // 自指（如 System Idle）不判孤儿
            {
                if (!byPid.TryGetValue(s.ParentPid, out var parent))
                {
                    hint = OrphanHint.ParentDead;
                }
                else if (s.CreationTimeUtc != UnreadableCreation && parent.CreationTimeUtc != UnreadableCreation
                         && parent.CreationTimeUtc > s.CreationTimeUtc)
                {
                    // 任一方哨兵不可判 → 不复用判定（裁决②），失败记录已兜底不进✅
                    hint = OrphanHint.PidReused;
                }
            }

            if (hint != OrphanHint.No)
            {
                snapshots[i] = s with { Signals = s.Signals with { OrphanHint = hint } };
            }
        }
    }

    /// <summary>前置条件：仅接受已是 Utc 或未指定 Kind 的值（当前管线产值均来自 FromFileTimeUtc/DateTime.UtcNow）；本方法重贴 Utc 标签不做时区换算。</summary>
    private static DateTime EnsureUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
