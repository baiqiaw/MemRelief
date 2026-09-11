using MemRelief.Core.Scanner;

namespace MemRelief.Core.Tests.Scanner;

// SignalRules 路径归一化助手测试（口径 #3/#12 共用，WBS T-03「路径归一化在采集侧完成」）。

public class SignalRulesPathTests
{
    [Theory]
    [InlineData(@"  C:\app\app.exe  ", @"C:\app\app.exe")]
    [InlineData(@"""C:\app\app.exe""", @"C:\app\app.exe")]
    [InlineData(@" ""C:\app\app.exe"" ", @"C:\app\app.exe")]
    [InlineData(@"C:\app\app.exe", @"C:\app\app.exe")]
    [InlineData(@"D:\AppData\\Kingsoft\\ksolaunch.exe", @"D:\AppData\Kingsoft\ksolaunch.exe")]
    [InlineData(@"""C:\Program Files\App\\app.exe""", @"C:\Program Files\App\app.exe")]
    [InlineData(@"\\server\share\app.exe", @"\\server\share\app.exe")]
    public void 归一化_去空白引号并折叠连续分隔符(string raw, string expected)
    {
        Assert.Equal(expected, SignalRules.NormalizeExecutablePath(raw));
    }

    [Fact]
    public void 归一化_双斜杠噪声与干净路径精确相等()
    {
        // 真机实证形态（WPS 计划任务动作路径含连续反斜杠）与进程侧规范路径须精确匹配命中
        Assert.True(SignalRules.PathExactEquals(
            @"D:\AppData\Local\Kingsoft\WPS Office\\ksolaunch.exe",
            @"D:\AppData\Local\Kingsoft\WPS Office\ksolaunch.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void 归一化_空形态返回null(string? raw)
    {
        Assert.Null(SignalRules.NormalizeExecutablePath(raw));
    }

    [Theory]
    [InlineData(@"C:\app\app.exe", @"C:\app")]
    [InlineData(@"C:\app.exe", "C:")]
    [InlineData(@"C:\app\sub\app.exe", @"C:\app\sub")]
    public void 目录段_最后分隔符前缀(string path, string expected)
    {
        Assert.Equal(expected, SignalRules.DirectoryOf(path));
    }

    [Fact]
    public void 精确相等_大小写不敏感且空白引号不敏感()
    {
        Assert.True(SignalRules.PathExactEquals(@"C:\App\app.exe", @"c:\app\APP.EXE"));
        Assert.True(SignalRules.PathExactEquals(@"""C:\app\app.exe""", @"C:\app\app.exe "));
    }

    [Fact]
    public void 精确相等_目录同文件名不同不命中()
    {
        Assert.False(SignalRules.PathExactEquals(@"C:\app\a.exe", @"C:\app\b.exe"));
    }

    [Fact]
    public void 精确相等_任一侧不可归一为false()
    {
        Assert.False(SignalRules.PathExactEquals(null, @"C:\app\a.exe"));
        Assert.False(SignalRules.PathExactEquals(@"C:\app\a.exe", null));
    }

    // ---------- StartupApproved 禁用态判读（口径 #12；覆盖率豁免依据链：判读归纯函数单测承载） ----------

    [Theory]
    [InlineData(new byte[] { 0x03, 0x00, 0x00, 0x00 }, true)]    // 禁用（实测形态）
    [InlineData(new byte[] { 0x02, 0x00, 0x00, 0x00 }, false)]   // 启用
    [InlineData(new byte[] { 0x06, 0x00, 0x00, 0x00 }, false)]   // 启用（含时间戳变体）
    [InlineData(new byte[] { }, false)]                          // 空数据 = 启用
    public void 禁用态判读_首字节奇偶(byte[] data, bool expected)
    {
        Assert.Equal(expected, SignalRules.IsDisabledApprovedValue(data));
    }

    [Fact]
    public void 禁用态判读_键缺失非二进制视为启用()
    {
        // StartupApproved 仅记录被禁用过的条目：null（值缺失/非二进制）= 启用，不误滤
        Assert.False(SignalRules.IsDisabledApprovedValue(null));
    }
}
