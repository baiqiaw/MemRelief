using MemRelief.App.State;
using MemRelief.App.Tests.TestDoubles;
using MemRelief.App.ViewModels;
using MemRelief.Core.Contracts;
using Xunit;

namespace MemRelief.App.Tests.ViewModels;

/// <summary>
/// 组折叠交互与带参命令封装测试：IsExpanded 双向变更通知、RelayCommand&lt;T&gt; 执行/可用性双通道。
/// 覆盖率口径：变更所及路径（本包新增类型）逐文件 line ≥80%，与 gate.ps1 total 口径并行核验。
/// </summary>
public class GroupAndCommandTests
{
    // —— 组折叠：用户展开/收起经 INPC 通知绑定面 ——
    [Fact]
    public async Task 组折叠态可变更_通知绑定面()
    {
        var vm = await ListPresentationTests.ScannedVmAsync();
        var protection = vm.Groups.Single(g => g.Level == Level.Protected);

        var notified = false;
        protection.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LevelGroup.IsExpanded)) notified = true; };

        protection.IsExpanded = true;    // 🚫 组展开可见原因（R02 GWT 的交互面）
        Assert.True(notified);
        Assert.True(protection.IsExpanded);

        protection.IsExpanded = false;
        Assert.False(protection.IsExpanded);
    }

    [Fact]
    public async Task 推荐组收起后_行勾选保持_不受组折叠影响()
    {
        var vm = await ListPresentationTests.ScannedVmAsync();
        var recommend = vm.Groups.Single(g => g.Level == Level.Recommend);

        recommend.IsExpanded = false;

        Assert.All(recommend.Rows, r => Assert.True(r.IsChecked));   // 勾选态与折叠态正交
    }

    // —— 无参命令 Execute 与 CanExecuteChanged 订阅（CommandManager 双通道挂钩）——
    [Fact]
    public async Task 无参命令执行_搜索命令路由到查询任务()
    {
        var (vm, _, rules, _) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();
        vm.SearchText = "b.exe";

        vm.SearchCommand.Execute(null);   // fire-and-forget；轮询等查询收口
        await WaitUntil.ForAsync(() => rules.QueryCalls.Count > 0);

        Assert.Single(rules.QueryCalls);
    }

    [Fact]
    public void 命令可用性变化事件_可订阅可退订_不抛()
    {
        var (vm, _, _, _) = ListPresentationTests.NewVm();
        EventHandler? handler = (_, _) => { };

        vm.WhitelistCommand.CanExecuteChanged += handler;   // 挂 CommandManager.RequerySuggested
        vm.WhitelistCommand.CanExecuteChanged -= handler;
        vm.StartScanCommand.CanExecuteChanged += handler;
        vm.StartScanCommand.CanExecuteChanged -= handler;
    }

    // —— RelayCommand<T>：Execute 参数路由 + CanExecute 双因素（行可加白 × 矩阵允许）——
    [Fact]
    public async Task 加白命令执行_经命令路由触发完整链路()
    {
        var (vm, _, rules, whitelist) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();
        var row = vm.Groups[0].Rows.First(r => r.Pid == 200);
        rules.Result = ListPresentationTests.NewClassifications()
            .Select(c => c.Pid == 200
                ? new Classification(200, Level.Whitelisted, [new Basis(14, "白名单命中：b.exe")],
                    c.TreePrivateBytes, c.WouldBeRevived, c.SourceEntries, c.RequiresElevation)
                : c)
            .ToList();

        vm.WhitelistCommand.Execute(row);

        // fire-and-forget：等待重判收口（轮询至 Classify 第二次被调或超时）
        await WaitUntil.ForAsync(() => rules.ClassifyCalls.Count >= 2);

        Assert.Equal(2, rules.ClassifyCalls.Count);
        Assert.Equal(1, whitelist.AddCount);
    }

    [Fact]
    public async Task 加白命令_非行参数与null_不可执行且执行安全()
    {
        var (vm, _, _, whitelist) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();

        Assert.False(vm.WhitelistCommand.CanExecute("b.exe"));   // 参数类型不符
        Assert.False(vm.WhitelistCommand.CanExecute(null));

        vm.WhitelistCommand.Execute("b.exe");                    // 类型不符不触发执行（守卫收口）
        vm.WhitelistCommand.Execute(null);
        Assert.Equal(0, whitelist.AddCount);
    }

    [Fact]
    public async Task 加白命令_释放中矩阵禁用_可执行为假()
    {
        var (vm, _, _, _) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();
        vm.StateMachine.TryTransition(AppTrigger.ReleaseConfirmed);   // ResultsShown → Releasing

        Assert.False(vm.Availability.ListInputEnabled);
        Assert.False(vm.WhitelistCommand.CanExecute(vm.Groups[0].Rows[0]));
    }

    // —— 行勾选双向（T-16 一键释放消费勾选集） ——
    [Fact]
    public async Task 谨慎行可手动勾选_保护行禁用勾选()
    {
        var vm = await ListPresentationTests.ScannedVmAsync();
        var cautionRow = vm.Groups.Single(g => g.Level == Level.Caution).Rows.Single();
        cautionRow.IsChecked = true;
        Assert.True(cautionRow.IsChecked);

        var protectedRow = vm.Groups.Single(g => g.Level == Level.Protected).Rows.Single();
        Assert.False(protectedRow.CanCheck);
    }
}
