using System.IO;
using MemRelief.App.State;
using MemRelief.App.Tests.TestDoubles;
using MemRelief.App.ViewModels;
using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;

namespace MemRelief.App.Tests.ViewModels;

/// <summary>
/// 白名单管理面板测试（F4/T-26）：条目元数据投影（添加时间/路径/备注+空值口径）、逐项移除、
/// 移除后重扫恢复参与判定（R04 GWT）、空白名单空态；列表尾部"因白名单排除 N 项"折叠区清单投影；
/// "打开日志/数据目录"入口（F6）。移除语义=PRD F4"移除后重新扫描即恢复参与判定"（不即时重判）。
/// </summary>
public class WhitelistPanelTests
{
    private static readonly DateTime T = new(2026, 9, 12, 2, 30, 0, DateTimeKind.Utc);

    private static WhitelistEntry Entry(string name, string? path = null, string? note = null) =>
        new(name, T, path, note);

    // —— F4：面板条目元数据投影（添加时间/路径/备注），空值显式占位 ——
    [Fact]
    public void 面板打开_列出全部条目_元数据投影_空值占位()
    {
        var whitelist = new FakeWhitelistStore();
        whitelist.Seed(
            Entry("a.exe", @"C:\apps\a.exe", "常驻下载器"),
            Entry("b.exe"));
        var vm = ListPresentationTests.NewVm(whitelist: whitelist).Vm;

        vm.ToggleWhitelistPanel();

        Assert.True(vm.IsWhitelistPanelOpen);
        var rows = vm.WhitelistEntries;
        Assert.Equal(2, rows.Count);
        Assert.Equal("a.exe", rows[0].Name);
        Assert.Equal(@"C:\apps\a.exe", rows[0].PathText);
        Assert.Equal("常驻下载器", rows[0].NoteText);
        Assert.Equal("b.exe", rows[1].Name);
        Assert.Equal("—", rows[1].PathText);   // 路径可空（记录性），空值显式呈现
        Assert.Equal("—", rows[1].NoteText);
        // 添加时间以本地时区可读格式投影（存储 Utc，展示本地——同快照时间戳口径）
        Assert.Equal(
            new DateTime(2026, 9, 12, 2, 30, 0, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            rows[0].AddedAtText);
    }

    // —— 面板每次打开都取存储最新态（面板常开期间外部变更不夹生）——
    [Fact]
    public void 面板每次打开_刷新存储最新态()
    {
        var whitelist = new FakeWhitelistStore();
        var vm = ListPresentationTests.NewVm(whitelist: whitelist).Vm;

        vm.ToggleWhitelistPanel();
        Assert.Empty(vm.WhitelistEntries);      // 空白名单

        whitelist.Seed(Entry("c.exe"));
        vm.ToggleWhitelistPanel();              // 关
        vm.ToggleWhitelistPanel();              // 再开：重取存储
        Assert.Equal(["c.exe"], vm.WhitelistEntries.Select(r => r.Name).ToList());
    }

    [Fact]
    public void 面板关闭_开关状态回落()
    {
        var vm = ListPresentationTests.NewVm().Vm;
        vm.ToggleWhitelistPanel();
        Assert.True(vm.IsWhitelistPanelOpen);
        vm.ToggleWhitelistPanel();
        Assert.False(vm.IsWhitelistPanelOpen);
    }

    // —— F4：逐项移除——按名写存储，面板行即时消失 ——
    [Fact]
    public async Task 逐项移除_按名写存储_面板行消失()
    {
        var whitelist = new FakeWhitelistStore();
        whitelist.Seed(Entry("a.exe"), Entry("b.exe"));
        var vm = ListPresentationTests.NewVm(whitelist: whitelist).Vm;
        vm.ToggleWhitelistPanel();

        var row = vm.WhitelistEntries.Single(r => r.Name == "a.exe");
        await vm.RemoveWhitelistEntryAsync(row);

        Assert.Equal(["a.exe"], whitelist.RemovedNames);   // 按名移除（OrdinalIgnoreCase 语义在存储层）
        Assert.Equal(["b.exe"], vm.WhitelistEntries.Select(r => r.Name).ToList());  // 面板行消失
        Assert.Null(vm.WhitelistNotice);                   // 成功路径无提示（行消失即反馈）
    }

    // —— 对抗：移除后未重扫——当前扫描结果与排除计数不变（恢复判定只走重扫，不即时重判）——
    [Fact]
    public async Task 移除后未重扫_当前列表与计数不变()
    {
        var whitelist = new FakeWhitelistStore();
        whitelist.Seed(Entry("e.exe"));
        var rules = new FakeRules { Result = ListPresentationTests.NewClassifications() };
        var vm = ListPresentationTests.NewVm(rules: rules, whitelist: whitelist).Vm;
        await vm.StartScanAsync();

        vm.ToggleWhitelistPanel();
        await vm.RemoveWhitelistEntryAsync(vm.WhitelistEntries.Single(r => r.Name == "e.exe"));

        // 存储已移除，但当前扫描面不变化（PRD F4：移除后重新扫描才恢复参与判定）
        Assert.Empty(whitelist.List());
        Assert.Equal(1, vm.WhitelistedExcludedCount);
        Assert.Equal(3, vm.Groups.Count);                                  // 三级组原样
        Assert.DoesNotContain(vm.Groups.SelectMany(g => g.Rows), r => r.Pid == 500);
        Assert.Single(vm.WhitelistedExcludedRows);
    }

    // —— 对抗：多项排除——折叠区清单与计数同源一致 ——
    [Fact]
    public async Task 折叠区_多项排除_清单与计数一致()
    {
        var rules = new FakeRules
        {
            Result =
            [
                new Classification(100, Level.Recommend, [new Basis(4, "无窗口用户级应用")],
                    1_000_000, false, [], false),
                new Classification(500, Level.Whitelisted, [new Basis(14, "白名单命中：e.exe")],
                    1_000_000, null, [], false),
                new Classification(501, Level.Whitelisted, [new Basis(14, "白名单命中：f.exe")],
                    1_000_000, null, [], false),
            ],
        };
        var vm = await ListPresentationTests.ScannedVmAsync(rules: rules);

        Assert.Equal(2, vm.WhitelistedExcludedCount);
        Assert.Equal(2, vm.WhitelistedExcludedRows.Count);                 // 清单条数=计数
        Assert.Equal([500, 501], vm.WhitelistedExcludedRows.Select(r => r.Pid).ToList());
    }

    // —— 对抗：移除与重扫并发（面板开着时用户点扫描）——终态一致不炸 ——
    [Fact]
    public async Task 移除与扫描并发_终态一致()
    {
        var whitelist = new FakeWhitelistStore();
        whitelist.Seed(Entry("e.exe"));
        var rules = new FakeRules { Result = ListPresentationTests.NewClassifications() };
        var vm = ListPresentationTests.NewVm(rules: rules, whitelist: whitelist).Vm;
        await vm.StartScanAsync();
        vm.ToggleWhitelistPanel();
        var row = vm.WhitelistEntries.Single(r => r.Name == "e.exe");

        var removeTask = vm.RemoveWhitelistEntryAsync(row);
        var scanTask = vm.StartScanAsync();
        await Task.WhenAll(removeTask, scanTask);

        Assert.Equal(AppState.ResultsShown, vm.StateMachine.State);        // 扫描链正常收口
        Assert.Empty(whitelist.List());                                    // 移除已落存储
        Assert.Empty(vm.WhitelistEntries);                                 // 面板刷新与存储一致（扫描链不触碰面板）
    }

    // —— R04 GWT 核心：移除白名单条目后重扫，恢复参与判定 ——
    [Fact]
    public async Task 移除后重扫_条目恢复参与判定_排除计数归零()
    {
        var whitelist = new FakeWhitelistStore();
        whitelist.Seed(Entry("e.exe"));                    // 存储条目：e.exe 已加白
        var rules = new FakeRules { Result = ListPresentationTests.NewClassifications() }; // e.exe(500)=Whitelisted
        var vm = ListPresentationTests.NewVm(rules: rules, whitelist: whitelist).Vm;
        await vm.StartScanAsync();
        Assert.Equal(1, vm.WhitelistedExcludedCount);      // 前置：e.exe 被排除

        vm.ToggleWhitelistPanel();
        await vm.RemoveWhitelistEntryAsync(vm.WhitelistEntries.Single(r => r.Name == "e.exe"));

        // 存储侧已移除；重扫判定输入的白名单快照不再含 e.exe
        Assert.Empty(whitelist.List());
        vm.ToggleWhitelistPanel(); // 关面板（面板状态与判定链无关）

        // 重扫：白名单不再命中 e.exe（判定输出由引擎给出，替身按同语义投影为推荐级）
        rules.Result = ListPresentationTests.NewClassifications()
            .Select(c => c.Pid == 500
                ? new Classification(500, Level.Recommend, [new Basis(4, "无窗口用户级应用")],
                    c.TreePrivateBytes, c.WouldBeRevived, c.SourceEntries, c.RequiresElevation)
                : c)
            .ToList();
        await vm.StartScanAsync();

        var recommend = vm.Groups.Single(g => g.Level == Level.Recommend);
        Assert.Contains(recommend.Rows, r => r.Pid == 500);       // 恢复参与判定（进推荐组）
        Assert.Equal(0, vm.WhitelistedExcludedCount);             // 排除计数归零
        Assert.DoesNotContain(rules.ClassifyCalls[^1].Whitelist.Entries, e => e.Name == "e.exe");
    }

    // —— 对抗：移除盘写失败 → 提示原因，条目保留 ——
    [Fact]
    public async Task 移除失败_提示原因_条目保留()
    {
        var whitelist = new FakeWhitelistStore();
        whitelist.Seed(Entry("a.exe"));
        whitelist.OnRemoveError = new IOException("磁盘已满");
        var vm = ListPresentationTests.NewVm(whitelist: whitelist).Vm;
        vm.ToggleWhitelistPanel();

        await vm.RemoveWhitelistEntryAsync(vm.WhitelistEntries.Single());

        Assert.Contains("移除白名单失败", vm.WhitelistNotice);
        Assert.Contains("磁盘已满", vm.WhitelistNotice);
        Assert.Equal(["a.exe"], vm.WhitelistEntries.Select(r => r.Name).ToList());  // 条目保留
    }

    // —— 对抗：移除目标已不存在（面板常开期间被移除）→ 刷新面板，不误报成功/失败 ——
    [Fact]
    public async Task 移除不存在条目_面板刷新_无提示()
    {
        var whitelist = new FakeWhitelistStore();
        whitelist.Seed(Entry("a.exe"));
        var vm = ListPresentationTests.NewVm(whitelist: whitelist).Vm;
        vm.ToggleWhitelistPanel();

        await vm.RemoveWhitelistEntryAsync(new WhitelistEntryRow("ghost.exe", T.ToString(), "—", "—"));

        Assert.Equal(["ghost.exe"], whitelist.RemovedNames);       // 存储被调用（返回 false）
        Assert.Equal(["a.exe"], vm.WhitelistEntries.Select(r => r.Name).ToList());  // 面板照常刷新
        Assert.Null(vm.WhitelistNotice);                           // 无成功亦无失败提示
    }

    // —— 空白名单空态标记（XAML 空态文案绑定用）——
    [Fact]
    public void 空白名单_面板空态标记()
    {
        var vm = ListPresentationTests.NewVm().Vm;
        vm.ToggleWhitelistPanel();
        Assert.Empty(vm.WhitelistEntries);
        Assert.False(vm.HasWhitelistEntries);
    }

    [Fact]
    public void 移除命令参数为空_守卫返回()
    {
        var whitelist = new FakeWhitelistStore();
        whitelist.Seed(Entry("a.exe"));
        var vm = ListPresentationTests.NewVm(whitelist: whitelist).Vm;
        vm.ToggleWhitelistPanel();

        _ = vm.RemoveWhitelistEntryCommand;                        // 命令面在位
        _ = vm.RemoveWhitelistEntryAsync(null);                    // null 参数守卫

        Assert.Empty(whitelist.RemovedNames);
        Assert.Equal(["a.exe"], vm.WhitelistEntries.Select(r => r.Name).ToList());
    }

    // —— F6：打开日志入口——文件存在 → 打开日志文件 ——
    [Fact]
    public async Task 打开日志_文件存在_打开日志文件路径()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"memrelief-t26-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(logPath, "{}");
        try
        {
            var opened = new List<string>();
            var vm = ListPresentationTests.NewVm(
                logStore: new FakeReleaseLogStore(logPath),
                shellOpen: opened.Add).Vm;

            vm.OpenLogFileCommand.Execute(null);

            Assert.Equal([logPath], opened);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    // —— 对抗：日志文件尚未生成（从未释放过）→ 打开所在数据目录（用户至少抵达现场）——
    [Fact]
    public void 打开日志_文件不存在_打开数据目录()
    {
        var dataDir = NewTempDataDir();
        try
        {
            var opened = new List<string>();
            var vm = ListPresentationTests.NewVm(
                logStore: new FakeReleaseLogStore(Path.Combine(dataDir, "releases.jsonl")),
                shellOpen: opened.Add).Vm;

            vm.OpenLogFileCommand.Execute(null);

            Assert.Equal([dataDir], opened);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    // —— F6：打开数据目录入口——委托收到日志所在目录 ——
    [Fact]
    public void 打开数据目录_打开日志所在目录()
    {
        var dataDir = NewTempDataDir();
        try
        {
            var opened = new List<string>();
            var vm = ListPresentationTests.NewVm(
                logStore: new FakeReleaseLogStore(Path.Combine(dataDir, "releases.jsonl")),
                shellOpen: opened.Add).Vm;

            vm.OpenDataDirectoryCommand.Execute(null);

            Assert.Equal([dataDir], opened);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    /// <summary>临时数据目录（OpenDataDirectory 会真实建目录，测后清理防污染）。</summary>
    private static string NewTempDataDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"memrelief-t26-{Guid.NewGuid():N}");
        return dir;
    }

    // —— 日志存储未注入 → 目录入口命令禁用（生产组合根恒注入；可空性与 releaser 先例一致）——
    [Fact]
    public void 日志存储未注入_目录入口命令禁用()
    {
        var vm = ListPresentationTests.NewVm().Vm;
        Assert.False(vm.OpenLogFileCommand.CanExecute(null));
        Assert.False(vm.OpenDataDirectoryCommand.CanExecute(null));
    }

    // —— 对抗：shell 打开失败（无默认程序/权限拒绝等环境态）→ 轻提示收口，不炸命令链 ——
    [Fact]
    public void shell打开失败_轻提示收口_不炸命令链()
    {
        var dataDir = NewTempDataDir();
        try
        {
            var vm = ListPresentationTests.NewVm(
                logStore: new FakeReleaseLogStore(Path.Combine(dataDir, "releases.jsonl")),
                shellOpen: _ => throw new InvalidOperationException("no shell")).Vm;

            vm.OpenDataDirectoryCommand.Execute(null);

            Assert.Contains("打开失败", vm.WhitelistNotice);
            Assert.Contains("no shell", vm.WhitelistNotice);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void 日志存储在位_目录入口命令可用()
    {
        var vm = ListPresentationTests.NewVm(
            logStore: new FakeReleaseLogStore(@"C:\memrelief-fake\releases.jsonl"),
            shellOpen: _ => { }).Vm;
        Assert.True(vm.OpenLogFileCommand.CanExecute(null));
        Assert.True(vm.OpenDataDirectoryCommand.CanExecute(null));
    }

    // —— F2 折叠区：排除项清单投影（展开可见被排除进程，消除列表外黑箱）——
    [Fact]
    public async Task 折叠区_排除项清单投影_与计数一致()
    {
        var vm = await ListPresentationTests.ScannedVmAsync();     // e.exe(500)=Whitelisted

        Assert.Equal(1, vm.WhitelistedExcludedCount);
        var row = Assert.Single(vm.WhitelistedExcludedRows);
        Assert.Equal(500, row.Pid);
        Assert.Equal("e.exe", row.Name);                           // 名称来自快照索引
    }

    [Fact]
    public async Task 折叠区_零排除_清单为空()
    {
        var rules = new FakeRules
        {
            Result =
            [
                new Classification(100, Level.Recommend, [new Basis(4, "无窗口用户级应用")],
                    1_000_000, false, [], false),
            ],
        };
        var vm = await ListPresentationTests.ScannedVmAsync(rules: rules);

        Assert.Equal(0, vm.WhitelistedExcludedCount);
        Assert.Empty(vm.WhitelistedExcludedRows);
    }

    // —— 折叠区行：快照缺项（契约违约脏数据）按"未知进程"占位，不炸投影；
    //    PID 由行模板统一承载（占位若含 PID 会渲染成 "PID 999 (PID 999)" 重复文案）——
    [Fact]
    public async Task 折叠区_快照缺项占位()
    {
        var rules = new FakeRules
        {
            Result =
            [
                new Classification(100, Level.Recommend, [new Basis(4, "无窗口用户级应用")],
                    1_000_000, false, [], false),
                new Classification(999, Level.Whitelisted, [new Basis(14, "白名单命中：ghost.exe")],
                    1_000_000, null, [], false),
            ],
        };
        var vm = await ListPresentationTests.ScannedVmAsync(rules: rules);

        var row = Assert.Single(vm.WhitelistedExcludedRows);
        Assert.Equal(999, row.Pid);
        Assert.Equal("未知进程", row.Name);
    }

    // —— 对抗：面板刷新段兜底（fire-and-forget 下绑定订阅方异常不静默，对齐全路径兜底模式）——
    [Fact]
    public void 面板刷新订阅方异常_兜底提示_不炸命令链()
    {
        var whitelist = new FakeWhitelistStore();
        whitelist.Seed(Entry("a.exe"));
        var vm = ListPresentationTests.NewVm(whitelist: whitelist).Vm;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.WhitelistEntries))
            {
                throw new InvalidOperationException("订阅方异常");
            }
        };

        vm.ToggleWhitelistPanel();

        Assert.Contains("白名单面板刷新失败", vm.WhitelistNotice);
        Assert.Contains("订阅方异常", vm.WhitelistNotice);
    }

    // —— 反向验证：数据目录不可建（路径被同名文件占用）→ 建目录失败轻提示收口，不炸命令链 ——
    [Fact]
    public void 数据目录不可建_轻提示收口_不炸命令链()
    {
        var blockerDir = NewTempDataDir();
        var blockerFile = Path.Combine(blockerDir, "occupied");
        try
        {
            Directory.CreateDirectory(blockerDir);
            File.WriteAllText(blockerFile, "同路径已被文件占用，CreateDirectory 必败");
            var opened = new List<string>();
            var vm = ListPresentationTests.NewVm(
                logStore: new FakeReleaseLogStore(Path.Combine(blockerFile, "releases.jsonl")),
                shellOpen: opened.Add).Vm;

            vm.OpenDataDirectoryCommand.Execute(null);

            Assert.Empty(opened);                                              // 未走到 shell 打开
            Assert.Contains("打开失败", vm.WhitelistNotice);
        }
        finally
        {
            Directory.Delete(blockerDir, recursive: true);
        }
    }
}
