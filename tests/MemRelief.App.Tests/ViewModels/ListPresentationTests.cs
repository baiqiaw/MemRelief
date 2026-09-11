using MemRelief.App.Scanning;
using MemRelief.App.State;
using MemRelief.App.Tests.TestDoubles;
using MemRelief.App.ViewModels;
using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using MemRelief.Core.Storage;

namespace MemRelief.App.Tests.ViewModels;

/// <summary>
/// 三级推荐列表呈现测试（R02 GWT 转测试）：三级分组默认态、组内降序、行展开六要素、
/// 空值口径（孤儿/无来源）、服务拉起文案、白名单排除计数；对抗性边界（空结果/全🚫/孤儿树）。
/// 采证口径：本包 AC 以 ViewModel/状态层测试实证，真实窗口视觉呈现归 T-21/T-22 真机验收。
/// </summary>
public class ListPresentationTests
{
    private const long Mb = 1024 * 1024;

    private static readonly DateTime T = new(2026, 9, 11, 7, 0, 0, DateTimeKind.Utc);

    /// <summary>七进程快照：孤儿树（100→110）/无来源/服务拉起/保护名单/白名单/未命中，覆盖全部要素组合。</summary>
    public static ScanResult NewSnapshot() => new(
        TakenAtUtc: new DateTime(2026, 9, 11, 8, 0, 0, DateTimeKind.Utc),
        ProcessCount: 7,
        DurationMs: 10,
        Snapshots:
        [
            new ProcessSnapshot(100, 0, "a.exe", @"C:\apps\a.exe", T, 60 * Mb,
                Signals: new SignalSet(OrphanHint.ParentDead)),
            new ProcessSnapshot(110, 100, "child.exe", @"C:\apps\child.exe", T, 5 * Mb),
            new ProcessSnapshot(200, 0, "b.exe", @"C:\apps\b.exe", T, 20 * Mb),
            new ProcessSnapshot(300, 0, "c.exe", @"C:\apps\c.exe", T, 80 * Mb,
                Signals: new SignalSet(TcpEstablishedCount: 3, ServiceName: "sv1", ServiceRestartOnFailure: true)),
            new ProcessSnapshot(400, 0, "d.exe", null, T, 200 * Mb),
            new ProcessSnapshot(500, 0, "e.exe", @"C:\apps\e.exe", T, 5 * Mb),
            new ProcessSnapshot(600, 0, "f.exe", null, T, 1 * Mb),
        ],
        Failures: []);

    /// <summary>与快照对应的分类输出（Classify 全量形态：含 Whitelisted/Unmatched，由 UI 侧过滤渲染）。</summary>
    public static IReadOnlyList<Classification> NewClassifications() =>
    [
        new(100, Level.Recommend, [new Basis(1, "孤儿进程（父进程已退出）")],
            65 * Mb, false, [new SourceEntry(SourceType.RunKey, @"HKCU\Software\...\Run\xyz")], false),
        new(200, Level.Recommend, [new Basis(4, "无窗口用户级应用")],
            20 * Mb, false, [], false),
        new(300, Level.Caution,
            [new Basis(8, "杀掉后会被服务管理器拉起（服务名：sv1）"), new Basis(6, "有活跃网络连接（3 条 ESTABLISHED）")],
            80 * Mb, true, [new SourceEntry(SourceType.Service, "sv1")], false),
        new(400, Level.Protected, [new Basis(0, "系统保护名单命中：d.exe")],
            200 * Mb, null, [], true),
        new(500, Level.Whitelisted, [new Basis(14, "白名单命中：e.exe")],
            5 * Mb, null, [], false),
        new(600, Level.Unmatched, [], 1 * Mb, null, [], false),
    ];

    internal static (MainViewModel Vm, FakeScanner Scanner, FakeRules Rules, FakeWhitelistStore Whitelist) NewVm(
        FakeScanner? scanner = null, FakeRules? rules = null, FakeWhitelistStore? whitelist = null)
    {
        scanner ??= new FakeScanner { OnTakeSnapshot = () => Task.FromResult(NewSnapshot()) };
        rules ??= new FakeRules { Result = NewClassifications() };
        whitelist ??= new FakeWhitelistStore();
        var coordinator = new ScanCoordinator(
            scanner, rules, new StaticRulePackStore(),
            new ClassificationContext(1, "u"), () => whitelist.Snapshot());
        var vm = new MainViewModel(new UiStateMachine(), coordinator, scanner, rules, whitelist);
        return (vm, scanner, rules, whitelist);
    }

    /// <summary>扫描成功铺态（列表已渲染）。</summary>
    internal static async Task<MainViewModel> ScannedVmAsync(
        FakeScanner? scanner = null, FakeRules? rules = null, FakeWhitelistStore? whitelist = null)
    {
        var (vm, _, _, _) = NewVm(scanner, rules, whitelist);
        await vm.StartScanAsync();
        return vm;
    }

    // —— R02：三级分组渲染，白名单/未命中不进组 ——
    [Fact]
    public async Task 扫描完成_三级分组渲染_白名单与未命中项不进组()
    {
        var vm = await ScannedVmAsync();

        Assert.Equal(3, vm.Groups.Count);
        Assert.Equal(Level.Recommend, vm.Groups[0].Level);
        Assert.Equal(Level.Caution, vm.Groups[1].Level);
        Assert.Equal(Level.Protected, vm.Groups[2].Level);
        Assert.Equal([100, 200], vm.Groups[0].Rows.Select(r => r.Pid));
        Assert.Equal([300], vm.Groups[1].Rows.Select(r => r.Pid));
        Assert.Equal([400], vm.Groups[2].Rows.Select(r => r.Pid));
        // 全量输出中的 Whitelisted(500)/Unmatched(600) 不出现在任何组
        Assert.DoesNotContain(vm.Groups.SelectMany(g => g.Rows), r => r.Pid is 500 or 600);
    }

    // —— R02：✅默认勾选/⚠️默认不勾/🚫折叠禁勾 ——
    [Fact]
    public async Task 组默认态_推荐展开全勾_谨慎展开不勾_保护折叠且复选框禁用()
    {
        var vm = await ScannedVmAsync();

        var recommend = vm.Groups.Single(g => g.Level == Level.Recommend);
        Assert.True(recommend.IsExpanded);
        Assert.All(recommend.Rows, r => Assert.True(r.IsChecked));
        Assert.All(recommend.Rows, r => Assert.True(r.CanCheck));

        var caution = vm.Groups.Single(g => g.Level == Level.Caution);
        Assert.True(caution.IsExpanded);
        Assert.All(caution.Rows, r => Assert.False(r.IsChecked));
        Assert.All(caution.Rows, r => Assert.True(r.CanCheck));

        var protection = vm.Groups.Single(g => g.Level == Level.Protected);
        Assert.False(protection.IsExpanded);                       // 🚫 默认折叠仅计数
        Assert.All(protection.Rows, r => Assert.False(r.CanCheck)); // 复选框禁用不可勾
        Assert.All(protection.Rows, r => Assert.False(r.IsChecked));
    }

    // —— R02：组内按树合计降序 ——
    [Fact]
    public async Task 组内按树合计内存降序()
    {
        // a=65MB(100+110 树合计)、b=20MB → 降序 [100, 200]
        var vm = await ScannedVmAsync();
        var recommend = vm.Groups.Single(g => g.Level == Level.Recommend);
        Assert.True(recommend.Rows[0].TreePrivateBytes > recommend.Rows[1].TreePrivateBytes);

        // 反序注入验证排序非巧合：b(80MB) > a(20MB) → b 在前
        var rules = new FakeRules
        {
            Result =
            [
                new Classification(100, Level.Recommend, [new Basis(1, "孤儿进程（父进程已退出）")],
                    20 * Mb, false, [], false),
                new Classification(200, Level.Recommend, [new Basis(4, "无窗口用户级应用")],
                    80 * Mb, false, [], false),
            ],
        };
        var vm2 = await ScannedVmAsync(rules: rules);
        Assert.Equal([200, 100], vm2.Groups[0].Rows.Select(r => r.Pid));
    }

    // —— R02 GWT：展开六要素齐全 ——
    [Fact]
    public async Task 行展开六要素齐全_主进程_进程树_原因_来源_树合计_拉起提示()
    {
        var vm = await ScannedVmAsync();
        var row = vm.Groups[0].Rows.First(r => r.Pid == 100);

        Assert.Equal("a.exe", row.ProcessName);                                   // ① 主进程名
        Assert.Equal(2, row.TreeLines.Count);                                     // ② 进程树（父子层级：100→110）
        Assert.Contains("a.exe (100)", row.TreeLines[0]);
        Assert.Contains("child.exe (110)", row.TreeLines[1]);
        Assert.Contains("孤儿进程（父进程已退出）", row.ReasonText);                // ③ 原因说明
        Assert.Contains("注册表自启动", row.SourceText);                           // ④ 来源类型+条目名
        Assert.Contains(@"Run\xyz", row.SourceText);
        Assert.Contains("65.0 MB", row.TreeSummary);                              // ⑤ 树合计内存
        Assert.Contains("不会被拉起", row.ReviveHint);                             // ⑥ 拉起提示
    }

    // —— R02：孤儿主进程显示"(父进程已退出)"；PID 复用同口径 ——
    [Fact]
    public async Task 孤儿行_主进程名后缀父进程已退出()
    {
        var vm = await ScannedVmAsync();
        Assert.Equal("a.exe(父进程已退出)", vm.Groups[0].Rows.First(r => r.Pid == 100).DisplayName);
    }

    [Fact]
    public async Task PID复用孤儿行_主进程名同口径后缀()
    {
        var rules = new FakeRules
        {
            Result = [new Classification(100, Level.Recommend, [new Basis(1, "孤儿进程（PID 复用，真父已退出）")],
                60 * Mb, false, [], false)],
        };
        var snapshot = NewSnapshot();
        var scanner = new FakeScanner
        {
            OnTakeSnapshot = () => Task.FromResult(snapshot),
        };
        // 快照 100 号进程改为 PidReused 形态
        var reused = new ScanResult(snapshot.TakenAtUtc, snapshot.ProcessCount, snapshot.DurationMs,
        [
            new ProcessSnapshot(100, 999, "a.exe", @"C:\apps\a.exe", T, 60 * Mb,
                Signals: new SignalSet(OrphanHint.PidReused)),
            .. snapshot.Snapshots.Skip(1),
        ], snapshot.Failures);
        scanner.OnTakeSnapshot = () => Task.FromResult(reused);
        var (vm, _, _, _) = NewVm(scanner, rules);
        await vm.StartScanAsync();

        Assert.Equal("a.exe(父进程已退出)", vm.Groups[0].Rows.Single().DisplayName);
    }

    // —— R02：无来源项显示"无（手动启动）" ——
    [Fact]
    public async Task 无来源行_显示无手动启动()
    {
        var vm = await ScannedVmAsync();
        var row = vm.Groups[0].Rows.First(r => r.Pid == 200);
        Assert.Contains("无（手动启动）", row.SourceText);
    }

    // —— R02 GWT：服务失败恢复 → 完整谨慎级文案（含 services.msc 建议后缀）——
    [Fact]
    public async Task 服务拉起行_原因文案含服务名与禁用来源建议()
    {
        var vm = await ScannedVmAsync();
        var row = vm.Groups[1].Rows.Single(r => r.Pid == 300);
        Assert.Contains("杀掉后会被服务管理器拉起（服务名：sv1），建议先禁用来源（services.msc）", row.ReasonText);
        Assert.Contains("Windows 服务：sv1", row.SourceText);
        Assert.Contains("会被重新拉起", row.ReviveHint);   // WouldBeRevived=true → 独立拉起提示
    }

    // —— 对抗：服务未配置失败恢复（SignalId=8 普通服务变体）不误加后缀 ——
    [Fact]
    public async Task 普通服务进程行_文案不误加失败恢复后缀()
    {
        var rules = new FakeRules
        {
            Result = [new Classification(300, Level.Caution,
                [new Basis(8, "服务进程——建议经 services.msc 禁用来源后重启")],
                80 * Mb, false, [new SourceEntry(SourceType.Service, "sv1")], false)],
        };
        var vm = await ScannedVmAsync(rules: rules);
        var row = vm.Groups.Single(g => g.Level == Level.Caution).Rows.Single();
        Assert.Contains("服务进程——建议经 services.msc 禁用来源后重启", row.ReasonText);
        Assert.DoesNotContain("建议先禁用来源（services.msc）", row.ReasonText);
        Assert.Contains("不会被拉起", row.ReviveHint);     // WouldBeRevived=false
    }

    // —— 对抗：拉起提示未评估态 ——
    [Fact]
    public async Task 拉起提示未评估_显示未评估口径()
    {
        var vm = await ScannedVmAsync();
        var row = vm.Groups[2].Rows.Single(r => r.Pid == 400);   // WouldBeRevived=null
        Assert.Contains("未评估", row.ReviveHint);
    }

    // —— F2：白名单排除计数（Classify 全量输出中的 Whitelisted 项计数）——
    [Fact]
    public async Task 扫描完成_白名单排除计数为全量输出中白名单项数()
    {
        var vm = await ScannedVmAsync();
        Assert.Equal(1, vm.WhitelistedExcludedCount);   // e.exe
    }

    // —— 对抗：空扫描结果（零推荐）——
    [Fact]
    public async Task 空扫描结果_组为空_空态文案_不崩()
    {
        var rules = new FakeRules { Result = [] };
        var vm = await ScannedVmAsync(rules: rules);

        Assert.Empty(vm.Groups);
        Assert.Equal(0, vm.WhitelistedExcludedCount);
        Assert.Equal(AppState.ResultsShown, vm.StateMachine.State);
        Assert.Equal("当前无可释放的进程", vm.StatusText);
    }

    // —— 对抗：全 🚫 列表（无 ✅/⚠️）——
    [Fact]
    public async Task 全不推荐列表_仅保护组_不崩()
    {
        var rules = new FakeRules
        {
            Result = [new Classification(400, Level.Protected, [new Basis(11, "受保护进程（打开受拒，疑似 PPL）——口径 #11")],
                200 * Mb, null, [], true)],
        };
        var vm = await ScannedVmAsync(rules: rules);

        var group = Assert.Single(vm.Groups);
        Assert.Equal(Level.Protected, group.Level);
        Assert.False(group.IsExpanded);
        Assert.All(group.Rows, r => Assert.False(r.CanCheck));
    }

    // —— 对抗：孤儿无后代（树仅自身）不崩；父子链跨级（孙代）全展开 ——
    [Fact]
    public async Task 进程树跨级_孙代全展开_环防御不崩()
    {
        // 100 → 110 → 111 三级链 + PID 互指环防御（111 的父改指 100 之外安全构造：parent=110）
        var snapshot = new ScanResult(NewSnapshot().TakenAtUtc, 3, 10,
        [
            new ProcessSnapshot(100, 0, "a.exe", @"C:\apps\a.exe", T, 60 * Mb,
                Signals: new SignalSet(OrphanHint.ParentDead)),
            new ProcessSnapshot(110, 100, "child.exe", @"C:\apps\child.exe", T, 5 * Mb),
            new ProcessSnapshot(111, 110, "grand.exe", @"C:\apps\grand.exe", T, 1 * Mb),
        ], []);
        var scanner = new FakeScanner { OnTakeSnapshot = () => Task.FromResult(snapshot) };
        var rules = new FakeRules
        {
            Result = [new Classification(100, Level.Recommend, [new Basis(1, "孤儿进程（父进程已退出）")],
                66 * Mb, false, [], false)],
        };
        var vm = await ScannedVmAsync(scanner, rules);

        var row = vm.Groups[0].Rows.Single();
        Assert.Equal(3, row.TreeLines.Count);
        Assert.Contains("a.exe (100)", row.TreeLines[0]);
        Assert.Contains("child.exe (110)", row.TreeLines[1]);
        Assert.Contains("grand.exe (111)", row.TreeLines[2]);
    }

    [Fact]
    public async Task 进程树互指环_visited防御终止_不重复渲染不挂死()
    {
        // 真环构造：100.parent=110 且 110.parent=100（脏数据契约违约形态），从 100 Walk 必须终止
        var snapshot = new ScanResult(NewSnapshot().TakenAtUtc, 2, 10,
        [
            new ProcessSnapshot(100, 110, "a.exe", @"C:\apps\a.exe", T, 60 * Mb,
                Signals: new SignalSet(OrphanHint.ParentDead)),
            new ProcessSnapshot(110, 100, "child.exe", @"C:\apps\child.exe", T, 5 * Mb),
        ], []);
        var scanner = new FakeScanner { OnTakeSnapshot = () => Task.FromResult(snapshot) };
        var rules = new FakeRules
        {
            Result = [new Classification(100, Level.Recommend, [new Basis(1, "孤儿进程（父进程已退出）")],
                65 * Mb, false, [], false)],
        };
        var vm = await ScannedVmAsync(scanner, rules);

        var row = vm.Groups[0].Rows.Single();
        Assert.Equal(2, row.TreeLines.Count);   // 环内每进程恰渲染一次，无死循环（测试完成即证）
        Assert.Contains("a.exe (100)", row.TreeLines[0]);
        Assert.Contains("child.exe (110)", row.TreeLines[1]);
    }

    [Fact]
    public async Task 重复PID脏数据_投影收口不拖垮整列表()
    {
        // 契约违约形态：快照含重复 PID → 行投影按首条收口（防脏数据把整扫拖成失败）
        var snapshot = new ScanResult(NewSnapshot().TakenAtUtc, 2, 10,
        [
            new ProcessSnapshot(100, 0, "a.exe", @"C:\apps\a.exe", T, 60 * Mb,
                Signals: new SignalSet(OrphanHint.ParentDead)),
            new ProcessSnapshot(100, 0, "a.exe-dup", @"C:\apps\a.exe", T, 60 * Mb),
        ], []);
        var scanner = new FakeScanner { OnTakeSnapshot = () => Task.FromResult(snapshot) };
        var rules = new FakeRules
        {
            Result = [new Classification(100, Level.Recommend, [new Basis(1, "孤儿进程（父进程已退出）")],
                60 * Mb, false, [], false)],
        };
        var vm = await ScannedVmAsync(scanner, rules);

        Assert.Equal("a.exe", vm.Groups[0].Rows.Single().ProcessName);   // 首条胜出，不抛异常
    }

    // —— 拉起提示按来源分流禁用入口建议（服务→services.msc、计划任务→taskschd.msc）——
    [Fact]
    public async Task 计划任务拉起行_建议指向taskschd()
    {
        var rules = new FakeRules
        {
            Result = [new Classification(300, Level.Caution, [new Basis(15, "存在对应计划任务，杀后会被拉起")],
                80 * Mb, true, [new SourceEntry(SourceType.ScheduledTask, "XUpdateTask")], false)],
        };
        var vm = await ScannedVmAsync(rules: rules);
        var row = vm.Groups.Single(g => g.Level == Level.Caution).Rows.Single();
        Assert.Contains("会被重新拉起", row.ReviveHint);
        Assert.Contains("taskschd.msc", row.ReviveHint);
        Assert.DoesNotContain("services.msc", row.ReviveHint);
    }
}
