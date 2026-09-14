using MemRelief.App.Text;
using MemRelief.Core.Contracts;
using Xunit;

namespace MemRelief.App.Tests.Text;

// 来源展示文案单测（issue #32 第 3 条收口）：双视图同条目的展示去重归 ui 层（数据忠实原则不动）
public class DisplayTextTests
{
    [Fact]
    public void 来源文案_双视图同条目_展示去重保一条()
    {
        // HKLM/HKCU 双视图对同一注册表值名各产出一条 SourceEntry（采集侧数据忠实），
        // 展示层按 Type+EntryName 去重，重复文案对用户无信息量
        var entries = new[]
        {
            new SourceEntry(SourceType.RunKey, "SomeApp"),
            new SourceEntry(SourceType.RunKey, "SomeApp"),
            new SourceEntry(SourceType.Service, "Svc"),
        };

        var text = DisplayText.Source(entries);

        Assert.Equal("注册表自启动：SomeApp；Windows 服务：Svc", text);
    }

    [Fact]
    public void 来源文案_不同类型同名条目_不去重_禁用入口不同须分别展示()
    {
        // 去重键含 Type：同名的服务与计划任务是不同来源（services.msc / taskschd.msc 各自禁用）
        var entries = new[]
        {
            new SourceEntry(SourceType.Service, "Updater"),
            new SourceEntry(SourceType.ScheduledTask, "Updater"),
        };

        var text = DisplayText.Source(entries);

        Assert.Contains("Windows 服务：Updater", text);
        Assert.Contains("计划任务：Updater", text);
    }

    [Fact]
    public void 来源文案_空集_无来源占位()
    {
        Assert.Equal(DisplayText.NoSource, DisplayText.Source([]));
    }
}
