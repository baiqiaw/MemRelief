using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;

namespace MemRelief.Core.Tests.Scanner;

// SnapshotAssembler 合成数据测试：字段映射、失败→SignalFailure、OrphanHint（口径#1 数据侧）、
// Pid 唯一键、契约不变量（Utc Kind/哨兵/防御性拷贝）。裁决依据 data-contracts §2 T-01 裁决记。

public class SnapshotAssemblerTests
{
    private static readonly DateTime BaseTime = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static RawProcess OpenedRow(
        int pid,
        int ppid,
        string name = "app.exe",
        string? path = @"C:\app\app.exe",
        DateTime? creation = null,
        long? commit = 4096,
        string? owner = "alice",
        string? cmdline = "--flag") => new(
        pid, ppid, name, ProcessOpenOutcome.Opened,
        ProcessField<string?>.Ok(path),
        ProcessField<DateTime?>.Ok(creation ?? BaseTime),
        ProcessField<long?>.Ok(commit),
        ProcessField<string?>.Ok(owner),
        ProcessField<double?>.Ok(0.3),
        cmdline);

    private static RawProcess OpenDeniedRow(int pid, int ppid, string name = "ppl.exe") => new(
        pid, ppid, name, ProcessOpenOutcome.AccessDenied,
        ProcessField<string?>.Fail("打开被拒"),
        ProcessField<DateTime?>.Fail("打开被拒"),
        ProcessField<long?>.Fail("打开被拒"),
        ProcessField<string?>.Fail("打开被拒"),
        CpuStart: ProcessField<double?>.Fail("打开被拒"),
        CommandLine: null);

    private static RawProcess VanishedRow(int pid, int ppid, string name = "gone.exe") => new(
        pid, ppid, name, ProcessOpenOutcome.Vanished,
        ProcessField<string?>.Fail("进程已退出"),
        ProcessField<DateTime?>.Fail("进程已退出"),
        ProcessField<long?>.Fail("进程已退出"),
        ProcessField<string?>.Fail("进程已退出"),
        CpuStart: ProcessField<double?>.Fail("进程已退出"),
        CommandLine: null);

    // ---------- 字段映射 ----------

    [Fact]
    public void 全字段成功_映射逐字段一致且CreationKind为Utc()
    {
        var rows = new[]
        {
            OpenedRow(4, 0, "system.exe", creation: BaseTime.AddHours(-1)),
            OpenedRow(10, 4, "app.exe", @"C:\app\app.exe", BaseTime, 8192, "alice", "--flag"),
        };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 42);

        var s = result.Snapshots.Single(x => x.Pid == 10);
        Assert.Equal(4, s.ParentPid);
        Assert.Equal("app.exe", s.Name);
        Assert.Equal(@"C:\app\app.exe", s.ExecutablePath);
        Assert.Equal(BaseTime, s.CreationTimeUtc);
        Assert.Equal(DateTimeKind.Utc, s.CreationTimeUtc.Kind);
        Assert.Equal(8192, s.PrivateCommittedBytes);
        Assert.Equal("alice", s.OwnerUser);
        Assert.Equal("--flag", s.CommandLine);
        Assert.Equal(OrphanHint.No, s.Signals.OrphanHint);
    }

    [Fact]
    public void ScanResult元数据_透传且ProcessCount等于行数()
    {
        var takenAt = new DateTime(2026, 9, 7, 3, 0, 0, DateTimeKind.Utc);
        var rows = new[] { OpenedRow(10, 0), OpenedRow(11, 10) };

        var result = SnapshotAssembler.Assemble(rows, takenAt, 1234);

        Assert.Equal(takenAt, result.TakenAtUtc);
        Assert.Equal(DateTimeKind.Utc, result.TakenAtUtc.Kind);
        Assert.Equal(2, result.ProcessCount);
        Assert.Equal(result.Snapshots.Count, result.ProcessCount);
        Assert.Equal(1234, result.DurationMs);
    }

    [Fact]
    public void 空输入_产出空ScanResult无失败()
    {
        var result = SnapshotAssembler.Assemble(Array.Empty<RawProcess>(), DateTime.UtcNow, 0);

        Assert.Empty(result.Snapshots);
        Assert.Empty(result.Failures);
        Assert.Equal(0, result.ProcessCount);
    }

    [Fact]
    public void 防御性拷贝_构造后修改源集合不影响ScanResult()
    {
        var rows = new List<RawProcess> { OpenedRow(10, 0) };
        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);
        var before = result.Snapshots.Count;

        rows.Add(OpenedRow(99, 0));

        Assert.Equal(before, result.Snapshots.Count);
        Assert.DoesNotContain(result.Snapshots, s => s.Pid == 99);
    }

    // ---------- 失败语义（T-01 裁决③④） ----------

    [Fact]
    public void 打开被拒_单条AccessDenied且字段取哨兵值()
    {
        var rows = new[] { OpenDeniedRow(20, 4) };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        var s = Assert.Single(result.Snapshots);
        Assert.Null(s.ExecutablePath);
        Assert.Equal(DateTime.MinValue, s.CreationTimeUtc);
        Assert.Equal(DateTimeKind.Utc, s.CreationTimeUtc.Kind);
        Assert.Equal(0, s.PrivateCommittedBytes);
        Assert.Null(s.OwnerUser);

        var failure = Assert.Single(result.Failures);
        Assert.Equal(ScannerSignalIds.ProcessOpen, failure.SignalId);
        Assert.Equal(20, failure.Pid);
        Assert.Equal(FailureKind.AccessDenied, failure.Kind);
        Assert.False(string.IsNullOrWhiteSpace(failure.Detail));
    }

    [Fact]
    public void 进程扫描中退出_单条Unreadable保留于快照()
    {
        var rows = new[] { VanishedRow(30, 4) };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        Assert.Single(result.Snapshots);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ScannerSignalIds.ProcessOpen, failure.SignalId);
        Assert.Equal(30, failure.Pid);
        Assert.Equal(FailureKind.Unreadable, failure.Kind);
    }

    [Fact]
    public void 路径失败_落编号100且路径为null()
    {
        var rows = new[] { OpenedRow(10, 0) with { ExecutablePath = ProcessField<string?>.Fail("查询失败") } };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        var s = Assert.Single(result.Snapshots);
        Assert.Null(s.ExecutablePath);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ScannerSignalIds.ExecutablePath, failure.SignalId);
        Assert.Equal(10, failure.Pid);
        Assert.Equal(FailureKind.Unreadable, failure.Kind);
    }

    [Fact]
    public void 创建时间失败_落编号101且为MinValueUtc哨兵()
    {
        var rows = new[] { OpenedRow(10, 0) with { CreationTimeUtc = ProcessField<DateTime?>.Fail("查询失败") } };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        var s = Assert.Single(result.Snapshots);
        Assert.Equal(DateTime.MinValue, s.CreationTimeUtc);
        Assert.Equal(DateTimeKind.Utc, s.CreationTimeUtc.Kind);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ScannerSignalIds.CreationTimeUtc, failure.SignalId);
        Assert.Equal(FailureKind.Unreadable, failure.Kind);
    }

    [Fact]
    public void 私有提交失败_落编号102且按口径13兜底记0()
    {
        var rows = new[] { OpenedRow(10, 0) with { PrivateCommittedBytes = ProcessField<long?>.Fail("查询失败") } };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        var s = Assert.Single(result.Snapshots);
        Assert.Equal(0, s.PrivateCommittedBytes);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ScannerSignalIds.PrivateCommittedBytes, failure.SignalId);
        Assert.Equal(FailureKind.Unreadable, failure.Kind);
    }

    [Fact]
    public void 所有者失败_落编号103且所有者为null()
    {
        var rows = new[] { OpenedRow(10, 0) with { OwnerUser = ProcessField<string?>.Fail("令牌不可读") } };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        var s = Assert.Single(result.Snapshots);
        Assert.Null(s.OwnerUser);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ScannerSignalIds.OwnerUser, failure.SignalId);
        Assert.Equal(FailureKind.Unreadable, failure.Kind);
    }

    [Fact]
    public void 多字段同时失败_逐字段各落一条编号()
    {
        var rows = new[] { OpenedRow(10, 0) with
        {
            ExecutablePath = ProcessField<string?>.Fail("路径失败"),
            OwnerUser = ProcessField<string?>.Fail("令牌不可读"),
        } };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        Assert.Equal(2, result.Failures.Count);
        Assert.Contains(result.Failures, f => f.SignalId == ScannerSignalIds.ExecutablePath);
        Assert.Contains(result.Failures, f => f.SignalId == ScannerSignalIds.OwnerUser);
        // 打开成功不产生进程级 0 号记录
        Assert.DoesNotContain(result.Failures, f => f.SignalId == ScannerSignalIds.ProcessOpen);
    }

    [Fact]
    public void 私有提交成功为0_不产生失败记录()
    {
        var rows = new[] { OpenedRow(10, 0, commit: 0) };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        Assert.Single(result.Snapshots);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void CommandLine为null_无失败记录()
    {
        var rows = new[] { OpenedRow(10, 0, cmdline: null) };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        Assert.Null(Assert.Single(result.Snapshots).CommandLine);
        Assert.Empty(result.Failures);
    }

    // ---------- OrphanHint（口径#1 数据侧） ----------

    [Fact]
    public void 父进程不在快照中_判ParentDead()
    {
        var rows = new[] { OpenedRow(100, 999) }; // 999 不存在

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        Assert.Equal(OrphanHint.ParentDead, Assert.Single(result.Snapshots).Signals.OrphanHint);
    }

    [Fact]
    public void 父创建时间晚于子_判PidReused()
    {
        var parent = OpenedRow(5, 0, creation: BaseTime.AddHours(2)); // 复用者更年轻
        var child = OpenedRow(6, 5, creation: BaseTime);
        var rows = new[] { parent, child };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        Assert.Equal(OrphanHint.PidReused, result.Snapshots.Single(s => s.Pid == 6).Signals.OrphanHint);
    }

    [Fact]
    public void 父在且早于子_判No()
    {
        var parent = OpenedRow(5, 0, creation: BaseTime);
        var child = OpenedRow(6, 5, creation: BaseTime.AddHours(1));
        var rows = new[] { parent, child };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        Assert.Equal(OrphanHint.No, result.Snapshots.Single(s => s.Pid == 6).Signals.OrphanHint);
    }

    [Fact]
    public void 子创建时间为哨兵_跳过复用比较判No()
    {
        var parent = OpenedRow(5, 0, creation: BaseTime);
        // 子创建时间失败 → 装配后为 MinValue 哨兵（101 号失败记录另行兜底不进✅）
        var child = OpenedRow(6, 5) with { CreationTimeUtc = ProcessField<DateTime?>.Fail("查询失败") };
        var rows = new[] { parent, child };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        Assert.Equal(OrphanHint.No, result.Snapshots.Single(s => s.Pid == 6).Signals.OrphanHint);
        Assert.Contains(result.Failures, f => f.Pid == 6 && f.SignalId == ScannerSignalIds.CreationTimeUtc);
    }

    [Fact]
    public void 父创建时间为哨兵_跳过复用比较判No()
    {
        var parent = OpenedRow(5, 0) with { CreationTimeUtc = ProcessField<DateTime?>.Fail("查询失败") };
        var child = OpenedRow(6, 5, creation: BaseTime);
        var rows = new[] { parent, child };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        Assert.Equal(OrphanHint.No, result.Snapshots.Single(s => s.Pid == 6).Signals.OrphanHint);
    }

    [Fact]
    public void 自指父进程_pdis等于自身_判No()
    {
        var rows = new[] { OpenedRow(0, 0, "System Idle Process") };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        Assert.Equal(OrphanHint.No, Assert.Single(result.Snapshots).Signals.OrphanHint);
    }

    // ---------- Pid 唯一键 ----------

    [Fact]
    public void Pid重复_违约定抛异常即扫描失败()
    {
        var rows = new[] { OpenedRow(10, 0), OpenedRow(10, 4) };

        Assert.Throws<InvalidOperationException>(
            () => SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1));
    }

    // ---------- 失败记录承载 ----------

    [Fact]
    public void 失败记录Pid精确绑定_不串扰其他进程()
    {
        var rows = new[] { OpenedRow(1, 0), OpenedRow(2, 0) with { OwnerUser = ProcessField<string?>.Fail("令牌不可读") } };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        var failure = Assert.Single(result.Failures);
        Assert.Equal(2, failure.Pid);
    }
}
