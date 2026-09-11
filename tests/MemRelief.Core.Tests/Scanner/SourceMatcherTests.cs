using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;

namespace MemRelief.Core.Tests.Scanner;

// SourceMatcher 纯匹配测试（口径 #12/#15，WBS T-03）：精确匹配（大小写不敏感）、不按进程名、
// 四类来源条目聚合（RunKey/ScheduledTask/StartupFolder + Service 复用 T-02 通道）、
// 路径不可得语义、计划任务拉起布尔。Inputs 工厂见 SourceTestFactory。

public class SourceMatcherTests
{
    private static SourceInputs Inputs(
        List<RunKeySource>? runKeys = null,
        List<ScheduledTaskSource>? tasks = null,
        List<StartupFolderSource>? folders = null,
        bool runKeyFailed = false,
        bool taskFailed = false,
        bool folderFailed = false) =>
        SourceTestFactory.Inputs(runKeys, tasks, folders, runKeyFailed, taskFailed, folderFailed);

    [Fact]
    public void Run键条目_路径精确匹配_产出RunKey来源()
    {
        var inputs = Inputs(runKeys: new() { new("OneDrive", @"C:\Users\a\AppData\Local\Microsoft\OneDrive\OneDrive.exe") });

        var (entries, revive) = SourceMatcher.Match(@"C:\Users\a\AppData\Local\Microsoft\OneDrive\OneDrive.exe", null, inputs);

        var entry = Assert.Single(entries);
        Assert.Equal(SourceType.RunKey, entry.Type);
        Assert.Equal("OneDrive", entry.EntryName);
        // 任务通道成功且路径可得 → 拉起已核实评估（false），非未评估 null
        Assert.False(revive);
    }

    [Fact]
    public void 大小写不同_仍精确匹配()
    {
        var inputs = Inputs(runKeys: new() { new("App", @"C:\Program Files\App\app.EXE") });

        var (entries, _) = SourceMatcher.Match(@"c:\program files\APP\app.exe", null, inputs);

        Assert.Single(entries);
    }

    [Fact]
    public void 仅目录相同_文件名不同_不匹配()
    {
        var inputs = Inputs(runKeys: new() { new("App", @"C:\Program Files\App\app.exe") });

        var (entries, _) = SourceMatcher.Match(@"C:\Program Files\App\uninstall.exe", null, inputs);

        Assert.Empty(entries);
    }

    [Fact]
    public void 同路径多类来源_全部产出且含条目名()
    {
        var inputs = Inputs(
            runKeys: new() { new("StartupApp", @"C:\tools\launcher.exe") },
            tasks: new() { new(@"\MyTasks\Updater", new List<string> { @"C:\tools\launcher.exe" }, RevivesOnLogonBootOrPeriodic: true) },
            folders: new() { new("Launcher.lnk", @"C:\tools\launcher.exe") });

        var (entries, revive) = SourceMatcher.Match(@"C:\TOOLS\launcher.exe", null, inputs);

        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.Type == SourceType.RunKey && e.EntryName == "StartupApp");
        Assert.Contains(entries, e => e.Type == SourceType.ScheduledTask && e.EntryName == @"\MyTasks\Updater");
        Assert.Contains(entries, e => e.Type == SourceType.StartupFolder && e.EntryName == "Launcher.lnk");
        Assert.True(revive);
    }

    [Fact]
    public void 服务进程_复用T02通道数据产出Service条目()
    {
        var inputs = Inputs();

        var (entries, _) = SourceMatcher.Match(@"C:\Windows\System32\svc.exe", "MySvc", inputs);

        var entry = Assert.Single(entries);
        Assert.Equal(SourceType.Service, entry.Type);
        Assert.Equal("MySvc", entry.EntryName);
    }

    [Fact]
    public void 非服务_无Service条目()
    {
        var inputs = Inputs();

        var (entries, _) = SourceMatcher.Match(@"C:\app\app.exe", null, inputs);

        Assert.Empty(entries);
    }

    [Fact]
    public void 路径不可得_空来源且Revive为null()
    {
        var inputs = Inputs(tasks: new() { new(@"\X\T", new List<string> { @"C:\t.exe" }, RevivesOnLogonBootOrPeriodic: true) });

        var (entries, revive) = SourceMatcher.Match(null, null, inputs);

        Assert.Empty(entries);
        Assert.Null(revive);
    }

    [Fact]
    public void 路径不可得的服务进程_Service条目仍产出()
    {
        // 服务关联按 pid（口径 #8），与路径可得性无关：PPL/路径不可读的服务进程来源展示不缺 Service 条目
        var inputs = Inputs();

        var (entries, revive) = SourceMatcher.Match(null, "ProtectedSvc", inputs);

        var entry = Assert.Single(entries);
        Assert.Equal(SourceType.Service, entry.Type);
        Assert.Equal("ProtectedSvc", entry.EntryName);
        Assert.Null(revive);
    }

    [Fact]
    public void 任务路径命中但触发器不含登录启动周期_revive为false()
    {
        // 任务存在（来源条目产出）但触发器为"事件"类（非登录/启动/周期）→ 来源有关联、拉起=false
        var inputs = Inputs(tasks: new() { new(@"\X\EventTask", new List<string> { @"C:\t.exe" }, RevivesOnLogonBootOrPeriodic: false) });

        var (entries, revive) = SourceMatcher.Match(@"C:\t.exe", null, inputs);

        Assert.Contains(entries, e => e.Type == SourceType.ScheduledTask);
        Assert.False(revive);
    }

    [Fact]
    public void 任务通道失败_拉起为null未评估不伪证false()
    {
        // 配对不变量：通道失败 → 任务清单空集 + 拉起=null（未评估），空集 Any()=false 会伪证"已核实不拉起"
        var inputs = Inputs(runKeys: new() { new("App", @"C:\app\app.exe") }, taskFailed: true);

        var (entries, revive) = SourceMatcher.Match(@"C:\app\app.exe", null, inputs);

        Assert.Single(entries);
        Assert.Null(revive);
    }

    [Fact]
    public void 任务多动作_任一路径命中即关联()
    {
        var inputs = Inputs(tasks: new() { new(@"\X\T", new List<string> { @"C:\other\setup.exe", @"C:\app\target.exe" }, RevivesOnLogonBootOrPeriodic: true) });

        var (entries, revive) = SourceMatcher.Match(@"C:\app\target.exe", null, inputs);

        Assert.Single(entries);
        Assert.True(revive);
    }
}
