namespace MemRelief.Bench.Tests;

/// <summary>入口参数解析（纯函数）。</summary>
public class BenchCliTests
{
    [Fact]
    public void 无参数_缺省20()
    {
        Assert.Equal(20, BenchCli.ParseTop([]));
    }

    [Fact]
    public void top参数生效()
    {
        Assert.Equal(5, BenchCli.ParseTop(["--top", "5"]));
    }

    [Fact]
    public void 非法数值_回退缺省()
    {
        Assert.Equal(20, BenchCli.ParseTop(["--top", "abc"]));
    }

    [Fact]
    public void 负值_回退缺省()
    {
        Assert.Equal(20, BenchCli.ParseTop(["--top", "-1"]));
    }

    [Fact]
    public void top在末尾缺值_回退缺省()
    {
        Assert.Equal(20, BenchCli.ParseTop(["--top"]));
    }

    [Fact]
    public void 其他参数_忽略_回退缺省()
    {
        Assert.Equal(20, BenchCli.ParseTop(["--json", "--verbose"]));
    }

    [Fact]
    public void 旗标大小写不敏感_对齐TestProcs先例()
    {
        Assert.Equal(5, BenchCli.ParseTop(["--TOP", "5"]));
        Assert.Equal(3, BenchCli.ParseRepeat(["--REPEAT", "3"]));
    }

    [Theory]
    [InlineData(new string[0], 1)]                     // 缺省 1 轮
    [InlineData(new[] { "--repeat", "3" }, 3)]         // 正常生效
    [InlineData(new[] { "--repeat", "abc" }, 1)]       // 非法回退
    [InlineData(new[] { "--repeat", "0" }, 1)]         // 下限收敛
    [InlineData(new[] { "--repeat", "-2" }, 1)]        // 负值收敛
    [InlineData(new[] { "--repeat", "99" }, 10)]       // 上限收敛
    public void repeat参数解析_边界收敛(string[] args, int expected)
    {
        Assert.Equal(expected, BenchCli.ParseRepeat(args));
    }
}
