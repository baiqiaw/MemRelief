using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;
using ScannerImpl = MemRelief.Core.Scanner.Scanner;

namespace MemRelief.Core.Tests.Scanner;

/// <summary>
/// 说明元数据合并测试（#58，ScannerImpl.MergeFileDescriptions 纯函数白盒）：按 pid 补写 FileDescription/CompanyName，
/// 缺键/null 字典行原样；快照其余字段与失败记录不受影响（非保护性数据面，无失败登记）。
/// </summary>
public class FileDescriptionMergeTests
{
    private static readonly DateTime T = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);

    private static ScanResult Snap(params ProcessSnapshot[] snapshots) =>
        new(T, snapshots.Length, 1, snapshots, []);

    private static ProcessSnapshot P(int pid, string? path) =>
        new(pid, 0, "a.exe", path, T, 1024, Signals: new SignalSet());

    [Fact]
    public void 空字典_快照内容原样()
    {
        var snap = Snap(P(1, @"C:\a.exe"));
        var merged = ScannerImpl.MergeFileDescriptions(snap, []);
        Assert.Same(snap, merged);
    }

    [Fact]
    public void null字典_快照原样()
    {
        var snap = Snap(P(1, @"C:\a.exe"));
        Assert.Same(snap, ScannerImpl.MergeFileDescriptions(snap, null));
    }

    [Fact]
    public void 按pid补写_缺键行原样_其余字段保持()
    {
        var snap = Snap(P(1, @"C:\a.exe"), P(2, @"C:\b.exe"));
        var dict = new Dictionary<int, (string?, string?)> { [1] = ("说明 A", "厂商 A") };

        var merged = ScannerImpl.MergeFileDescriptions(snap, dict);

        Assert.Equal(2, merged.Snapshots.Count);
        var first = merged.Snapshots[0];
        Assert.Equal("说明 A", first.FileDescription);
        Assert.Equal("厂商 A", first.CompanyName);
        Assert.Equal(@"C:\a.exe", first.ExecutablePath);   // 其余字段保持
        Assert.Equal(1024L, first.PrivateCommittedBytes);
        Assert.Null(merged.Snapshots[1].FileDescription);  // 缺键行原样
        Assert.Null(merged.Snapshots[1].CompanyName);
        Assert.Empty(merged.Failures);                     // 非保护性：无 SignalFailure 登记（AC3 证据）
    }

    [Fact]
    public void 重复补写_已有值被覆盖()
    {
        var snap = Snap(new ProcessSnapshot(1, 0, "a.exe", @"C:\a.exe", T, 1024,
            Signals: new SignalSet(), FileDescription: "旧说明", CompanyName: null));
        var dict = new Dictionary<int, (string?, string?)> { [1] = ("新说明", "厂商") };

        var merged = ScannerImpl.MergeFileDescriptions(snap, dict);

        Assert.Equal("新说明", merged.Snapshots[0].FileDescription);
        Assert.Equal("厂商", merged.Snapshots[0].CompanyName);
    }
}
