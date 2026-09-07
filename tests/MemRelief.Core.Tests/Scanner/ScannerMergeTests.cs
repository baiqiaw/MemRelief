using MemRelief.Core.Scanner;

namespace MemRelief.Core.Tests.Scanner;

// MergeCommandLines 合成数据测试（编排层唯一纯逻辑）：pid 补全、通道级 null 退化、缺行静默（Scanner 内部函数，IVT 白盒）

// Scanner 类名与命名空间段同名，别名消解
using ScannerImpl = MemRelief.Core.Scanner.Scanner;

public class ScannerMergeTests
{
    private static RawProcess Row(int pid, string? cmdline = null) => new(
        pid, 0, "p.exe", ProcessOpenOutcome.Opened,
        ProcessField<string?>.Ok(@"C:\p.exe"),
        ProcessField<DateTime?>.Ok(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
        ProcessField<long?>.Ok(4096),
        ProcessField<string?>.Ok("alice"),
        cmdline);

    [Fact]
    public void 通道结果null_全部行命令行保持null且无记录语义()
    {
        var rows = new List<RawProcess> { Row(1), Row(2) };

        var merged = ScannerImpl.MergeCommandLines(rows, null);

        Assert.Equal(2, merged.Count);
        Assert.All(merged, r => Assert.Null(r.CommandLine));
    }

    [Fact]
    public void 字典命中_按pid补全命令行()
    {
        var rows = new List<RawProcess> { Row(1), Row(2) };
        var dict = new Dictionary<int, string?> { [1] = "--flag", [2] = null };

        var merged = ScannerImpl.MergeCommandLines(rows, dict);

        Assert.Equal("--flag", merged.Single(r => r.Pid == 1).CommandLine);
        Assert.Null(merged.Single(r => r.Pid == 2).CommandLine); // 字典值 null=系统进程无命令行，合法
    }

    [Fact]
    public void 字典缺行_该行保持null不报错()
    {
        var rows = new List<RawProcess> { Row(1), Row(2) };
        var dict = new Dictionary<int, string?> { [1] = "--flag" }; // pid 2 已在 WMI 查询前退出

        var merged = ScannerImpl.MergeCommandLines(rows, dict);

        Assert.Equal("--flag", merged.Single(r => r.Pid == 1).CommandLine);
        Assert.Null(merged.Single(r => r.Pid == 2).CommandLine);
    }

    [Fact]
    public void 字典多余pid_忽略不产生行()
    {
        var rows = new List<RawProcess> { Row(1) };
        var dict = new Dictionary<int, string?> { [1] = "--flag", [99] = "--ghost" };

        var merged = ScannerImpl.MergeCommandLines(rows, dict);

        Assert.Single(merged);
        Assert.DoesNotContain(merged, r => r.Pid == 99);
    }
}
