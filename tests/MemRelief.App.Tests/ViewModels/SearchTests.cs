using MemRelief.App.Tests.TestDoubles;
using MemRelief.App.ViewModels;
using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;

namespace MemRelief.App.Tests.ViewModels;

/// <summary>
/// 搜索框全量判定查询测试（R02 GWT）：列表外进程判定结果与不可见原因（未命中规则/白名单排除）、
/// 无结果空态；Query 消费 Classify 全量输出不重算判定（Core 契约，VM 只做文案投影）。
/// </summary>
public class SearchTests
{
    [Fact]
    public async Task 搜索未命中规则进程_显示未命中原因()
    {
        var (vm, _, rules, _) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();
        rules.OnQuery = (_, _, _, _) => [new QueryResult(600, "f.exe", Level.Unmatched, [])];

        vm.SearchText = "f.exe";
        await vm.SearchAsync();

        var row = Assert.Single(vm.SearchResults);
        Assert.Equal("f.exe", row.Name);
        Assert.Equal("未命中规则，不进列表", row.OutcomeText);
        Assert.Equal("匹配 1 项", vm.SearchStatusText);
    }

    [Fact]
    public async Task 搜索白名单排除进程_显示白名单排除原因()
    {
        var (vm, _, rules, _) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();
        rules.OnQuery = (_, _, _, _) => [new QueryResult(500, "e.exe", Level.Whitelisted,
            [new Basis(14, "白名单命中：e.exe")])];

        vm.SearchText = "e.exe";
        await vm.SearchAsync();

        var row = Assert.Single(vm.SearchResults);
        Assert.Equal("白名单排除", row.OutcomeText);
        Assert.Contains("白名单命中：e.exe", row.ReasonText);
    }

    [Fact]
    public async Task 搜索列表内进程_显示级别与依据()
    {
        var (vm, _, rules, _) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();
        rules.OnQuery = (_, _, _, _) => [new QueryResult(300, "c.exe", Level.Caution,
            [new Basis(8, "杀掉后会被服务管理器拉起（服务名：sv1）")])];

        vm.SearchText = "c.exe";
        await vm.SearchAsync();

        var row = Assert.Single(vm.SearchResults);
        Assert.Equal("谨慎级（在列表中）", row.OutcomeText);
        Assert.Contains("杀掉后会被服务管理器拉起（服务名：sv1）", row.ReasonText);
    }

    [Fact]
    public async Task 搜索无结果_显示无匹配提示()
    {
        var (vm, _, rules, _) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();
        rules.OnQuery = (_, _, _, _) => [];

        vm.SearchText = "notexist.exe";
        await vm.SearchAsync();

        Assert.Empty(vm.SearchResults);
        Assert.Equal("无匹配进程", vm.SearchStatusText);
    }

    [Fact]
    public async Task 空输入_不触发查询_给出输入引导()
    {
        var (vm, _, rules, _) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();

        vm.SearchText = "   ";
        await vm.SearchAsync();

        Assert.Empty(rules.QueryCalls);
        Assert.Empty(vm.SearchResults);
        Assert.Equal("输入进程名或 PID 后查询", vm.SearchStatusText);
    }

    [Fact]
    public async Task 纯数字输入_按PID查询_其余按名称查询()
    {
        var (vm, _, rules, _) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();
        rules.OnQuery = (_, _, _, _) => [];

        vm.SearchText = "4321";
        await vm.SearchAsync();
        Assert.Equal((null, 4321), (rules.QueryCalls[^1].Name, rules.QueryCalls[^1].Pid));

        vm.SearchText = "b.exe";
        await vm.SearchAsync();
        Assert.Equal(("b.exe", null), (rules.QueryCalls[^1].Name, rules.QueryCalls[^1].Pid));
    }

    [Fact]
    public async Task 搜索传最近扫描快照与全量分类_不重算判定()
    {
        var (vm, _, rules, _) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();

        vm.SearchText = "e.exe";
        await vm.SearchAsync();

        var (scan, classifications, name, pid) = rules.QueryCalls.Single();
        Assert.Equal(ListPresentationTests.NewSnapshot().TakenAtUtc, scan.TakenAtUtc);
        Assert.Equal(6, classifications.Count);   // Classify 全量输出（含 Whitelisted/Unmatched）
        Assert.Equal("e.exe", name);
        Assert.Null(pid);
    }

    // —— 对抗：未扫描态搜索不越权（数据缺失按引导文案收口） ——
    [Fact]
    public async Task 未扫描时搜索_不触发查询_给出引导()
    {
        var (vm, _, rules, _) = ListPresentationTests.NewVm();

        vm.SearchText = "a.exe";
        await vm.SearchAsync();

        Assert.Empty(rules.QueryCalls);
        Assert.Equal("输入进程名或 PID 后查询", vm.SearchStatusText);
    }

    // —— 对抗：查询异常收口到状态行（fire-and-forget 链路无未观察异常） ——
    [Fact]
    public async Task 查询异常_状态行给出失败反馈_不静默()
    {
        var (vm, _, rules, _) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();
        rules.OnQuery = (_, _, _, _) => throw new InvalidOperationException("引擎违约");

        vm.SearchText = "a.exe";
        await vm.SearchAsync();

        Assert.Empty(vm.SearchResults);
        Assert.Contains("查询失败", vm.SearchStatusText);
        Assert.Contains("引擎违约", vm.SearchStatusText);
    }

    // —— 对抗：连续查询序号守卫，旧响应不覆盖新输入的结果 ——
    [Fact]
    public async Task 连续查询_后发起者胜_旧响应被丢弃()
    {
        var (vm, _, rules, _) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();

        // 查询 1 挂起（线程池线程内等放行）→ 查询 2 即时完成 → 放行查询 1：其响应必须被序号守卫丢弃
        var firstGate = new TaskCompletionSource();
        var gateInstalled = false;
        rules.OnQuery = (_, _, _, _) =>
        {
            if (!gateInstalled)
            {
                gateInstalled = true;
                firstGate.Task.Wait();   // 阻塞查询线程模拟慢查询（放行后旧响应走序号守卫丢弃）
            }

            return [];                   // 第二次查询即时无结果
        };

        vm.SearchText = "first.exe";
        var first = vm.SearchAsync();    // 挂起
        vm.SearchText = "second.exe";
        await vm.SearchAsync();          // 即时完成，seq=2

        Assert.Contains("无匹配进程", vm.SearchStatusText);
        firstGate.SetResult();
        await first;

        Assert.Contains("无匹配进程", vm.SearchStatusText);   // 旧响应未覆盖
        Assert.Empty(vm.SearchResults);
    }
}
