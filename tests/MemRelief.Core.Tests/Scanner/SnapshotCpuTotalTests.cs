using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;

namespace MemRelief.Core.Tests.Scanner;

/// <summary>口径 #16 采集侧填充测试（#59）：CpuTotalSeconds 由 RawProcess.CpuStart（自启动累计）
/// 派生填充 SignalSet；CpuStart 不可读（Fail）→ null（依据不成立，无独立 SignalFailure——差分 #7 已覆盖）。</summary>
public class SnapshotCpuTotalTests
{
    private static readonly DateTime T = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);

    private static RawProcess Row(int pid, ProcessField<double?> cpuStart) => new(
        pid, 0, "a.exe", ProcessOpenOutcome.Opened,
        ProcessField<string?>.Ok(@"C:\a.exe"),
        ProcessField<DateTime?>.Ok(T),
        ProcessField<long?>.Ok(4096),
        ProcessField<string?>.Ok("u"),
        cpuStart,
        null);

    private static SignalInputs Inputs() => new(
        VisiblePids: new HashSet<int>(),
        WindowEnumerationFailed: false,
        TcpEstablished: new Dictionary<int, int>(),
        TcpTableFailed: false,
        Services: new Dictionary<int, ServiceSignalInfo>(),
        ServiceEnumerationFailed: false,
        SystemDirectoryPrefixes: [],
        UwpPackagePrefix: null,
        DirectoryResolveFailed: false,
        CpuDeltas: new Dictionary<int, double?> { [1] = 0.1, [2] = 0.1 });

    [Fact]
    public void CpuStart正常_填充为CpuTotalSeconds()
    {
        var failures = new List<SignalFailure>();
        var merged = SnapshotAssembler.MergeSignals(Row(1, ProcessField<double?>.Ok(3.42)), Inputs(), failures);

        Assert.Equal(3.42, merged.CpuTotalSeconds);
    }

    [Fact]
    public void CpuStart不可读_保持null_无独立失败登记()
    {
        var failures = new List<SignalFailure>();
        var merged = SnapshotAssembler.MergeSignals(Row(2, ProcessField<double?>.Fail("句柄受拒")), Inputs(), failures);

        Assert.Null(merged.CpuTotalSeconds);
        Assert.DoesNotContain(failures, f => f.SignalId == 16);   // 口径 #16 无独立失败通道（差分 #7 承载保守）
    }
}
