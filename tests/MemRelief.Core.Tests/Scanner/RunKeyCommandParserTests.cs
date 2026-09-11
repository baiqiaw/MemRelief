using MemRelief.Core.Scanner;

namespace MemRelief.Core.Tests.Scanner;

// RunKeyCommandParser 纯函数测试（口径 #12）：Run 键命令串三种形态 + 畸形输入。

public class RunKeyCommandParserTests
{
    [Fact]
    public void 带引号含空格路径加参数_取引号内()
    {
        var path = RunKeyCommandParser.ExtractExecutablePath(@"""C:\Program Files\App\app.exe"" -minimized");

        Assert.Equal(@"C:\Program Files\App\app.exe", path);
    }

    [Fact]
    public void 带引号纯路径_取引号内()
    {
        var path = RunKeyCommandParser.ExtractExecutablePath(@"""C:\tools\run.exe""");

        Assert.Equal(@"C:\tools\run.exe", path);
    }

    [Fact]
    public void 无引号带参数_存在性判定选中最长前缀()
    {
        // 无引号含参数：前缀须存在性判定区分路径与参数
        var path = RunKeyCommandParser.ExtractExecutablePath(@"C:\tools\run.exe /flag value",
            fileExists: p => p == @"C:\tools\run.exe");

        Assert.Equal(@"C:\tools\run.exe", path);
    }

    [Fact]
    public void 无引号含空格路径_存在性判定拼接出完整路径()
    {
        var path = RunKeyCommandParser.ExtractExecutablePath(@"C:\Program Files\App\app.exe -q",
            fileExists: p => p == @"C:\Program Files\App\app.exe");

        Assert.Equal(@"C:\Program Files\App\app.exe", path);
    }

    [Fact]
    public void 无引号无存在性判定_取首个空格前token()
    {
        var path = RunKeyCommandParser.ExtractExecutablePath(@"C:\tools\run.exe /flag");

        Assert.Equal(@"C:\tools\run.exe", path);
    }

    [Fact]
    public void 畸形未闭合引号_剥引号整串()
    {
        var path = RunKeyCommandParser.ExtractExecutablePath(@"""C:\tools\run.exe");

        Assert.Equal(@"C:\tools\run.exe", path);
    }

    [Fact]
    public void 未闭合引号带参数_参数不混入路径()
    {
        // 修复轮补：未闭合引号回退无引号 token 逻辑（旧实现整串返回会把参数当路径，永不可匹配）
        var path = RunKeyCommandParser.ExtractExecutablePath(@"""C:\tools\run.exe -flag",
            fileExists: p => p == @"C:\tools\run.exe");

        Assert.Equal(@"C:\tools\run.exe", path);
    }

    [Fact]
    public void UNC候选_跳过存在性探测取首token且不触发fileExists()
    {
        // 修复轮补：File.Exists 对死 UNC 可能 SMB 等待秒级——非本地盘符候选不进探测
        var probed = new List<string>();
        var path = RunKeyCommandParser.ExtractExecutablePath(@"\\deadserver\share\app.exe -q",
            fileExists: p => { probed.Add(p); return false; });

        Assert.Equal(@"\\deadserver\share\app.exe", path);
        Assert.Empty(probed);
    }

    [Fact]
    public void 超长对抗输入_直接取首token不探测()
    {
        // 修复轮补：存在性探测循环对超长串（损坏/对抗数据）做 O(n²) 放大——长度上限内才探测
        var longCommand = @"C:\a.exe " + new string('x', RunKeyCommandParser.MaxProbeCommandLength + 1);
        var probed = new List<string>();
        var path = RunKeyCommandParser.ExtractExecutablePath(longCommand,
            fileExists: p => { probed.Add(p); return false; });

        Assert.Equal(@"C:\a.exe", path);
        Assert.Empty(probed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void 空输入_返回null(string? raw)
    {
        Assert.Null(RunKeyCommandParser.ExtractExecutablePath(raw));
    }
}
