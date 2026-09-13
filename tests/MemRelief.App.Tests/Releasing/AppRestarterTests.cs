using System.Diagnostics;
using MemRelief.App.Hosting;
using MemRelief.App.Releasing;

namespace MemRelief.App.Tests.Releasing;

/// <summary>
/// ElevationRestarter 测试（T-16 产生侧单点）：runAs 形态、参数与 RestartOptions round-trip 一致、
/// 32K 超限降级在产生侧收口。Process.Start 注入捕获，不真启进程。
/// </summary>
public class AppRestarterTests
{
    [Fact]
    public void 重启_组装runAs启动参数_参数可被解析侧还原()
    {
        ProcessStartInfo? captured = null;
        var restarter = new ElevationRestarter(
            @"C:\app\MemRelief.exe",
            psi => { captured = psi; return null; });
        var items = new[] { new RestartFailedItem("a.exe", @"C:\apps\a.exe") };

        restarter.Restart(items);

        Assert.NotNull(captured);
        Assert.True(captured!.UseShellExecute);
        Assert.Equal("runAs", captured.Verb);
        Assert.Equal(@"C:\app\MemRelief.exe", captured.FileName);
        // 解析侧还原：自动重扫+失败项清单一致（产生侧↔解析侧契约同一实现钉死）
        var parsed = RestartOptions.Parse(captured.Arguments.Split(' '));
        Assert.True(parsed.AutoRescan);
        Assert.Single(parsed.FailedItems);
        Assert.Equal("a.exe", parsed.FailedItems[0].Name);
        Assert.Equal(@"C:\apps\a.exe", parsed.FailedItems[0].ExecutablePath);
    }

    [Fact]
    public void 重启_失败项清单超32K_降级为仅自动重扫()
    {
        ProcessStartInfo? captured = null;
        var restarter = new ElevationRestarter(
            @"C:\app\MemRelief.exe",
            psi => { captured = psi; return null; });
        var hugePath = @"C:\" + new string('长', 200) + ".exe";
        var items = Enumerable.Range(0, 400)
            .Select(i => new RestartFailedItem($"p{i}.exe", hugePath)).ToList();

        restarter.Restart(items);

        var parsed = RestartOptions.Parse(captured!.Arguments.Split(' '));
        Assert.True(parsed.AutoRescan);
        Assert.Empty(parsed.FailedItems); // 超限降级：不携带清单，保自动重扫（裁决①）
    }
}
