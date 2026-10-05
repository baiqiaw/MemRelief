using MemRelief.Core.Scanner;

namespace MemRelief.Core.Tests.Scanner;

/// <summary>
/// exe 元数据读取通道测试（#58）：注入读函数断言逐行语义——正常行返回元组、路径缺失/打开失败行键缺席、
/// 单行读取异常跳过不击穿（非保护性数据面：无 SignalFailure、永不抛）。
/// </summary>
public class FileDescriptionSourceTests
{
    private static readonly DateTime T = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);

    private static RawProcess Row(
        int pid,
        ProcessField<string?> path) => new(
        pid, 0, "a.exe", ProcessOpenOutcome.Opened, path,
        ProcessField<DateTime?>.Ok(T), ProcessField<long?>.Ok(1L),
        ProcessField<string?>.Ok("u"), ProcessField<double?>.Ok(0.5), null);

    [Fact]
    public void 正常路径行_返回元组()
    {
        var source = new FileDescriptionSource(_ => ("说明文字", "厂商"));
        var dict = source.Read([Row(1, ProcessField<string?>.Ok(@"C:\a.exe"))]);

        var entry = Assert.Single(dict);
        Assert.Equal(1, entry.Key);
        Assert.Equal(("说明文字", "厂商"), entry.Value);
    }

    [Fact]
    public void 路径缺失或打开失败行_键缺席()
    {
        var source = new FileDescriptionSource(_ => ("说明", "厂商"));
        var dict = source.Read(
        [
            Row(1, ProcessField<string?>.Ok(null)),            // 路径不可读（值 null）
            Row(2, ProcessField<string?>.Fail("拒绝访问")),     // 字段级失败
        ]);

        Assert.Empty(dict);
    }

    [Fact]
    public void 单行读取异常_该行无说明_不击穿()
    {
        var source = new FileDescriptionSource(
            path => path == @"C:\bad.exe" ? throw new IOException("读失败") : ("说明", "厂商"));
        var dict = source.Read(
        [
            Row(1, ProcessField<string?>.Ok(@"C:\bad.exe")),
            Row(2, ProcessField<string?>.Ok(@"C:\ok.exe")),
        ]);

        Assert.Equal(2, dict.Count);
        Assert.Equal(((string?)null, (string?)null), dict[1]);   // 异常行=无说明（同路径缓存，展示与键缺席等价）
        Assert.Equal(("说明", "厂商"), dict[2]);
    }

    [Fact]
    public void 空白元组值_规整为null()
    {
        var source = new FileDescriptionSource(_ => ("  ", null));
        var dict = source.Read([Row(1, ProcessField<string?>.Ok(@"C:\a.exe"))]);

        Assert.Equal(((string?)null, (string?)null), dict[1]);
    }

    [Fact]
    public void 同路径多实例_只读取一次()
    {
        // #58 评审修复：IO 次数=唯一路径数（chrome 类集群形态常态）
        var reads = new List<string>();
        var source = new FileDescriptionSource(path =>
        {
            reads.Add(path);
            return ("说明", "厂商");
        });
        var dict = source.Read(
        [
            Row(1, ProcessField<string?>.Ok(@"C:\app\runtime.exe")),
            Row(2, ProcessField<string?>.Ok(@"C:\app\runtime.exe")),
            Row(3, ProcessField<string?>.Ok(@"C:\app\runtime.exe")),
        ]);

        Assert.Equal(3, dict.Count);
        Assert.All(dict.Values, v => Assert.Equal(("说明", "厂商"), v));
        var path = Assert.Single(reads);   // 同路径仅读一次
        Assert.Equal(@"C:\app\runtime.exe", path);
    }
}
