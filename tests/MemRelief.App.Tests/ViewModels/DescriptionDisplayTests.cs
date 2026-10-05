using MemRelief.App.Scanning;
using MemRelief.App.State;
using MemRelief.App.Tests.TestDoubles;
using MemRelief.App.ViewModels;
using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using MemRelief.Core.Storage;

namespace MemRelief.App.Tests.ViewModels;

/// <summary>
/// 进程说明每行展示测试（#58）：树行/头行/搜索行/详情路径的说明投影与取值优先级（手册 &gt; exe 说明 &gt; 公司名），
/// 无说明时保持原格式；内置名单面板第五节（说明手册）。采证口径同 ListPresentationTests：VM 层实证，视觉归真机。
/// </summary>
public class DescriptionDisplayTests
{
    private const long Mb = 1024 * 1024;
    private static readonly DateTime T = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string HandbookJson = """
        [
          { "match": "wechat", "description": "微信（聊天通讯）" }
        ]
        """;

    /// <summary>三进程快照：手册命中+exe 说明（优先级）、仅公司名兜底、无任何说明。</summary>
    private static ScanResult NewSnapshot() => new(
        TakenAtUtc: new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc),
        ProcessCount: 3,
        DurationMs: 5,
        Snapshots:
        [
            new ProcessSnapshot(100, 0, "WeChat.exe", @"C:\apps\WeChat.exe", T, 60 * Mb,
                FileDescription: "WeChat 客户端", CompanyName: "腾讯"),
            new ProcessSnapshot(200, 0, "tool.exe", @"C:\apps\tool.exe", T, 20 * Mb,
                FileDescription: null, CompanyName: "Tool 厂商"),
            new ProcessSnapshot(300, 0, "bare.exe", null, T, 10 * Mb),
        ],
        Failures: []);

    private static IReadOnlyList<Classification> NewClassifications() =>
    [
        new(100, Level.Recommend, [new Basis(4, "无窗口用户级应用")], 60 * Mb, false, [], false),
        new(200, Level.Recommend, [new Basis(4, "无窗口用户级应用")], 20 * Mb, false, [], false),
        new(300, Level.Caution, [new Basis(5, "疑似常驻应用")], 10 * Mb, false, [], false),
    ];

    private static (MainViewModel Vm, FakeRules Rules) NewVm(IDescriptionStore descriptionStore)
    {
        var scanner = new FakeScanner { OnTakeSnapshot = () => Task.FromResult(NewSnapshot()) };
        var rules = new FakeRules { Result = NewClassifications() };
        var coordinator = new ScanCoordinator(
            scanner, rules, new StaticRulePackStore(),
            new ClassificationContext(1, "u"), () => new WhitelistSnapshot([]));
        var vm = new MainViewModel(new UiStateMachine(), coordinator, rules, new FakeWhitelistStore(),
            rulePackStore: new StaticRulePackStore(),
            descriptionStore: descriptionStore);
        return (vm, rules);
    }

    private static MainViewModel ScannedVm(IDescriptionStore? store = null) =>
        NewVm(store ?? new DescriptionStore(_ => HandbookJson)).Vm;   // 默认注入手册

    [Fact]
    public async Task 树行每行带说明_手册优先于exe自带说明()
    {
        var vm = await ScannedVm().StartScanAndRender();

        Assert.Equal("WeChat.exe (100) 微信（聊天通讯）", Row(vm, 100).TreeLines[0]);
    }

    [Fact]
    public async Task 树行说明_公司名兜底_无说明保持原格式()
    {
        var vm = await ScannedVm().StartScanAndRender();

        Assert.Equal("tool.exe (200) Tool 厂商", Row(vm, 200).TreeLines[0]);
        Assert.Equal("bare.exe (300)", Row(vm, 300).TreeLines[0]);   // 无任何说明：原格式
    }

    [Fact]
    public async Task 头行说明_单实例行与聚合组行()
    {
        var vm = await ScannedVm().StartScanAndRender();

        Assert.Equal("微信（聊天通讯）", Row(vm, 100).DescriptionText);
        Assert.Equal("Tool 厂商", Row(vm, 200).DescriptionText);
        Assert.Null(Row(vm, 300).DescriptionText);
    }

    [Fact]
    public async Task 详情路径行_可读显示路径_不可读占位()
    {
        var vm = await ScannedVm().StartScanAndRender();

        Assert.Equal(@"C:\apps\WeChat.exe", Row(vm, 100).PathText);
        Assert.Equal("—", Row(vm, 300).PathText);   // 路径不可读：与白名单面板空值口径同源
    }

    [Fact]
    public async Task 搜索结果行_带说明()
    {
        var (vm, rules) = NewVm(new DescriptionStore(_ => HandbookJson));
        await vm.StartScanAsync();
        rules.OnQuery = (_, _, _, _) => [new QueryResult(100, "WeChat.exe", Level.Recommend, [])];

        vm.SearchText = "wechat";
        await vm.SearchAsync();

        var row = Assert.Single(vm.SearchResults);
        Assert.Equal("微信（聊天通讯）", row.Description);
    }

    [Fact]
    public async Task 未注入手册_exe说明与公司名兜底仍生效()
    {
        var vm = await NewVm(new DescriptionStore(_ => "[]")).Vm.StartScanAndRender();

        Assert.Equal("WeChat 客户端", Row(vm, 100).DescriptionText);
        Assert.Equal("tool.exe (200) Tool 厂商", Row(vm, 200).TreeLines[0]);
    }

    [Fact]
    public async Task 内置名单面板_第五节展示说明手册()
    {
        var vm = await ScannedVm().StartScanAndRender();

        vm.ToggleBuiltinListsPanel();

        var section = vm.BuiltinListSections.Single(s => s.Title.Contains("进程说明"));
        Assert.Contains("wechat", section.ItemsText);
        Assert.Contains("微信（聊天通讯）", section.ItemsText);
    }

    private static ClassificationRow Row(MainViewModel vm, int pid) =>
        vm.Groups.SelectMany(g => g.Rows).Single(r => r.Pid == pid);
}

/// <summary>测试便利：扫描并等待列表渲染。</summary>
internal static class DescriptionDisplayExtensions
{
    public static async Task<MainViewModel> StartScanAndRender(this MainViewModel vm)
    {
        await vm.StartScanAsync();
        return vm;
    }
}
