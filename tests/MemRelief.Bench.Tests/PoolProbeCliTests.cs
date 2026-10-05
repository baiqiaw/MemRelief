namespace MemRelief.Bench.Tests;

/// <summary>poolprobe 参数解析（纯函数）：--trees 规模列表与 --mode 模式过滤。</summary>
public class PoolProbeCliTests
{
    [Fact]
    public void 无参数_缺省规模列表()
    {
        Assert.Equal([10, 50, 100, 200, 300], PoolProbeCli.ParseTrees([]));
    }

    [Fact]
    public void 单值生效()
    {
        Assert.Equal([42], PoolProbeCli.ParseTrees(["--trees", "42"]));
    }

    [Fact]
    public void 逗号列表生效()
    {
        Assert.Equal([5, 10, 20], PoolProbeCli.ParseTrees(["--trees", "5,10,20"]));
    }

    [Fact]
    public void 逗号列表_含空段_整体回退缺省()
    {
        Assert.Equal([10, 50, 100, 200, 300], PoolProbeCli.ParseTrees(["--trees", "5,,20"]));
    }

    [Fact]
    public void 非法值_整体回退缺省()
    {
        Assert.Equal([10, 50, 100, 200, 300], PoolProbeCli.ParseTrees(["--trees", "5,abc"]));
    }

    [Fact]
    public void 零或负值_整体回退缺省()
    {
        Assert.Equal([10, 50, 100, 200, 300], PoolProbeCli.ParseTrees(["--trees", "0"]));
        Assert.Equal([10, 50, 100, 200, 300], PoolProbeCli.ParseTrees(["--trees", "-5"]));
    }

    [Fact]
    public void 超上限值_整体回退缺省()
    {
        Assert.Equal([10, 50, 100, 200, 300], PoolProbeCli.ParseTrees(["--trees", "5000"]));
    }

    [Fact]
    public void trees在末尾缺值_回退缺省()
    {
        Assert.Equal([10, 50, 100, 200, 300], PoolProbeCli.ParseTrees(["--trees"]));
    }

    [Fact]
    public void 旗标大小写不敏感_对齐BenchCli先例()
    {
        Assert.Equal([7], PoolProbeCli.ParseTrees(["--TREES", "7"]));
    }

    [Theory]
    [InlineData(new string[0], "both")]
    [InlineData(new[] { "--mode", "sync" }, "sync")]
    [InlineData(new[] { "--mode", "async" }, "async")]
    [InlineData(new[] { "--mode", "SYNC" }, "sync")]
    [InlineData(new[] { "--mode", "abc" }, "both")]
    [InlineData(new[] { "--mode" }, "both")]
    public void mode参数解析_非法回退both(string[] args, string expected)
    {
        Assert.Equal(expected, PoolProbeCli.ParseMode(args));
    }
}
