using System.IO;
using MemRelief.App.State;
using MemRelief.App.Tests.TestDoubles;
using MemRelief.App.ViewModels;
using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using MemRelief.Core.Storage;

namespace MemRelief.App.Tests.ViewModels;

/// <summary>
/// 右键加白链路测试（F4 + data-contracts §2 ③.s4 裁决⑥）：仅 ✅/⚠️ 级可加白、加白成功即时重跑
/// Classify 并从推荐列表移除该行、白名单排除计数同步、加白失败列表保留；白名单损坏自愈提示通道。
/// </summary>
public class WhitelistFlowTests
{
    // —— F4：右键加白仅 ✅/⚠️ 级可用 ——
    [Fact]
    public async Task 加白可用性_推荐与谨慎级可用_保护级禁用()
    {
        var vm = await ListPresentationTests.ScannedVmAsync();

        Assert.True(vm.Groups[0].Rows.All(r => r.CanWhitelist));    // ✅
        Assert.True(vm.Groups[1].Rows.All(r => r.CanWhitelist));    // ⚠️
        Assert.False(vm.Groups[2].Rows.All(r => r.CanWhitelist));   // 🚫 组行均不可
        var protectedRow = vm.Groups[2].Rows.Single();
        Assert.False(vm.WhitelistCommand.CanExecute(protectedRow));
        Assert.True(vm.WhitelistCommand.CanExecute(vm.Groups[0].Rows[0]));
    }

    [Fact]
    public async Task 执行加白_写入白名单存储_携带快照路径()
    {
        var (vm, _, _, whitelist) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();
        var row = vm.Groups[0].Rows.First(r => r.Pid == 200);

        await vm.WhitelistAsync(row);

        Assert.Equal(1, whitelist.AddCount);
        Assert.Equal("b.exe", whitelist.AddedNames.Single());
        Assert.Equal(@"C:\apps\b.exe", whitelist.Snapshot().Entries.Single().Path);
    }

    // —— ③.s4 裁决⑥：加白成功后即时重跑 Classify 并从推荐列表移除该行，计数同步 ——
    [Fact]
    public async Task 加白成功_即时重跑判定_该行移除_计数同步()
    {
        var (vm, _, rules, whitelist) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();

        // 重跑后 b.exe 归入白名单（真实 RulesEngine 语义的替身投影：命中白名单的项级变更为 Whitelisted）
        rules.Result = ListPresentationTests.NewClassifications()
            .Select(c => c.Pid == 200
                ? new Classification(200, Level.Whitelisted, [new Basis(14, "白名单命中：b.exe")],
                    c.TreePrivateBytes, c.WouldBeRevived, c.SourceEntries, c.RequiresElevation)
                : c)
            .ToList();

        await vm.WhitelistAsync(vm.Groups[0].Rows.First(r => r.Pid == 200));

        // 即时重跑：Classify 第二次被调，白名单快照取 Add 后的最新视图
        Assert.Equal(2, rules.ClassifyCalls.Count);
        var recommend = vm.Groups.Single(g => g.Level == Level.Recommend);
        Assert.DoesNotContain(recommend.Rows, r => r.Pid == 200);        // 该行即时移除
        Assert.Equal(1, recommend.Rows.Count);                           // 仅剩 a.exe（pid 100）
        Assert.Equal(2, vm.WhitelistedExcludedCount);                    // 计数含重跑判定的全部白名单项（e.exe 预置 + b.exe 新加）
        Assert.Equal(1, whitelist.Snapshot().Entries.Count);             // 存储侧仅本次 Add 的 b.exe（e.exe 是分类预置态，非存储条目）
        // 同一快照重判：时间戳不变，状态停留已展示
        Assert.Equal(ListPresentationTests.NewSnapshot().TakenAtUtc, vm.LastScanTakenAtUtc);
        Assert.Equal(AppState.ResultsShown, vm.StateMachine.State);
    }

    // —— 对抗：加白失败（盘写异常）→ 提示且列表保留 ——
    [Fact]
    public async Task 加白失败_提示原因_列表保留()
    {
        var (vm, _, rules, whitelist) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();
        whitelist.OnAddError = new IOException("磁盘已满");

        await vm.WhitelistAsync(vm.Groups[0].Rows.First(r => r.Pid == 200));

        Assert.Contains("加白失败", vm.WhitelistNotice);
        Assert.Contains("磁盘已满", vm.WhitelistNotice);
        Assert.Equal(1, rules.ClassifyCalls.Count);                       // 未重跑
        Assert.Equal(2, vm.Groups.Single(g => g.Level == Level.Recommend).Rows.Count); // 列表保留
        Assert.Equal(1, vm.WhitelistedExcludedCount);
    }

    // —— 失败分流：写白名单成功但重判失败 → 文案与事实一致（已生效，下次扫描可见）——
    [Fact]
    public async Task 重判失败_白名单已落盘_提示已加入而非加白失败()
    {
        var (vm, _, rules, whitelist) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();
        rules.OnClassifyError = new InvalidOperationException("判定引擎异常");

        await vm.WhitelistAsync(vm.Groups[0].Rows.First(r => r.Pid == 200));

        Assert.Equal(1, whitelist.AddCount);                              // 白名单已写入
        Assert.Contains("已加入白名单", vm.WhitelistNotice);               // 不再误报“加白失败”
        Assert.DoesNotContain("加白失败", vm.WhitelistNotice);
        Assert.Contains("下次扫描生效", vm.WhitelistNotice);
        Assert.Equal(2, vm.Groups.Single(g => g.Level == Level.Recommend).Rows.Count); // 列表保留
    }

    // —— 竞态代际守卫：重判期间新扫描换代 → 丢弃陈旧重判结果，不覆盖新扫描数据 ——
    [Fact]
    public async Task 重判期间新扫描换代_陈旧重判结果被丢弃_不覆盖新数据()
    {
        var (vm, scanner, rules, whitelist) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();

        // 重判挂起：等待测试放行，模拟重判慢于新扫描
        var classifyGate = new TaskCompletionSource();
        var newSnapshot = new ScanResult(
            new DateTime(2026, 9, 11, 9, 0, 0, DateTimeKind.Utc), 2, 10,
            [ListPresentationTests.NewSnapshot().Snapshots[0], ListPresentationTests.NewSnapshot().Snapshots[1]],
            []);
        var staleResult = ListPresentationTests.NewClassifications();     // 旧快照的重判输出

        // 第二次 Classify（重判）挂到 gate；第三次（新扫描的判定）即时返回新代际结果（级别可区分：
        // a.exe 归 Caution → 若陈旧重判覆盖新数据，Groups 会出现 Recommend 组）
        rules.OnClassifyGate = index => index == 2 ? classifyGate.Task : Task.CompletedTask;
        rules.ResultSelector = index => index == 3
            ? [new Classification(100, Level.Caution, [new Basis(8, "服务进程——建议经 services.msc 禁用来源后重启")],
                65_000_000, false, [], false)]
            : staleResult;

        var whitelistTask = vm.WhitelistAsync(vm.Groups[0].Rows.First(r => r.Pid == 200)); // 重判挂起中

        // 重判挂起期间用户发起新扫描（换快照、换列表、换时间戳）
        scanner.OnTakeSnapshot = () => Task.FromResult(newSnapshot);
        var scanTask = vm.StartScanAsync();

        // 放行被挂起的重判：其输出基于旧快照，必须被代际守卫丢弃
        classifyGate.SetResult();
        await Task.WhenAll(whitelistTask, scanTask);

        // 列表/快照/时间戳均为新扫描代际，未被旧快照重判覆盖（旧快照重判会重新出现 Recommend 组）
        Assert.Equal(newSnapshot.TakenAtUtc, vm.LastScanTakenAtUtc);
        Assert.Equal(AppState.ResultsShown, vm.StateMachine.State);
        Assert.DoesNotContain(vm.Groups, g => g.Level == Level.Recommend);   // 陈旧重判未覆盖
        var cautionGroup = Assert.Single(vm.Groups);
        Assert.Equal([100], cautionGroup.Rows.Select(r => r.Pid));           // 新代际列表内容
        // 白名单条目已落盘（Add 成功不受影响）
        Assert.Equal(1, whitelist.AddCount);
    }

    // —— 对抗：未扫描态无数据可加白，守卫直接返回 ——
    [Fact]
    public async Task 未扫描态加白_守卫返回_不写存储()
    {
        var (vm, _, _, whitelist) = ListPresentationTests.NewVm();
        var detached = ListPresentationTests.NewClassifications()[1];      // b.exe（未铺态构造行）

        await vm.WhitelistAsync(MakeRow(detached, ListPresentationTests.NewSnapshot()));

        Assert.Equal(0, whitelist.AddCount);
        Assert.Equal(0, whitelist.AddedNames.Count);
    }

    // —— 对抗：快照缺项的占位行（契约违约形态）不可加白，防占位名写入脏条目 ——
    [Fact]
    public async Task 快照缺项占位行加白_守卫返回_不写存储()
    {
        var (vm, _, rules, whitelist) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();
        var orphanPidClassification = new Classification(
            999, Level.Recommend, [new Basis(4, "无窗口用户级应用")], 1_000_000, false, [], false);

        await vm.WhitelistAsync(MakeRow(orphanPidClassification, ListPresentationTests.NewSnapshot()));

        Assert.Equal(0, whitelist.AddCount);
        Assert.Equal(1, rules.ClassifyCalls.Count);                        // 未触发重判
    }

    // —— 数据换代失效搜索面板：加白重判后旧搜索结论不与新列表共存 ——
    [Fact]
    public async Task 加白重判收口_搜索面板清空()
    {
        var (vm, _, rules, _) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();
        rules.OnQuery = (_, _, _, _) => [new QueryResult(200, "b.exe", Level.Recommend, [])];
        vm.SearchText = "b.exe";
        await vm.SearchAsync();
        Assert.NotEmpty(vm.SearchResults);                                 // 前置：搜索有结果

        rules.Result = ListPresentationTests.NewClassifications()
            .Select(c => c.Pid == 200
                ? new Classification(200, Level.Whitelisted, [new Basis(14, "白名单命中：b.exe")],
                    c.TreePrivateBytes, c.WouldBeRevived, c.SourceEntries, c.RequiresElevation)
                : c)
            .ToList();
        await vm.WhitelistAsync(vm.Groups[0].Rows.First(r => r.Pid == 200));

        Assert.Empty(vm.SearchResults);                                    // 旧搜索结论失效
        Assert.Equal(string.Empty, vm.SearchStatusText);                   // 面板随空状态折叠
    }

    // —— 在途查询作废：换代后旧查询响应不落绑定面 ——
    [Fact]
    public async Task 搜索后重扫_旧搜索响应作废_面板保持空()
    {
        var (vm, _, rules, _) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();

        // 模拟换代后在途旧响应：直接置旧结果再换代收口（ApplyResult 作废语义）
        vm.SearchText = "b.exe";
        await vm.SearchAsync();
        await vm.StartScanAsync();                                         // 重扫换代

        Assert.Empty(vm.SearchResults);
        Assert.Equal(string.Empty, vm.SearchStatusText);
    }

    [Fact]
    public async Task 保护级行加白_守卫返回_不写存储()
    {
        var (vm, _, _, whitelist) = ListPresentationTests.NewVm();
        await vm.StartScanAsync();
        var protectedRow = vm.Groups[2].Rows.Single();

        await vm.WhitelistAsync(protectedRow);

        Assert.Equal(0, whitelist.AddCount);
    }

    // —— 白名单损坏自愈提示（IWhitelistStore.Recovery 唯一通道；文案携带 Reason，禁固定口径掩盖备份失败变体）——
    [Fact]
    public void 启动装载损坏自愈_提示携带自愈原因()
    {
        var whitelist = new FakeWhitelistStore
        {
            Recovery = new WhitelistRecovery(@"C:\data\whitelist.json.corrupt", "白名单文件解析失败"),
        };
        var (vm, _, _, _) = ListPresentationTests.NewVm(whitelist: whitelist);

        Assert.Contains("白名单异常已处理", vm.WhitelistNotice);
        Assert.Contains("白名单文件解析失败", vm.WhitelistNotice);
    }

    [Fact]
    public void 启动装载备份失败变体_提示保留原地保留事实()
    {
        // WhitelistStore.Recover 备份失败分支：原文件原地保留、未重建——固定“已重置”文案与事实相反
        var whitelist = new FakeWhitelistStore
        {
            Recovery = new WhitelistRecovery(@"C:\data\whitelist.json.corrupt", "白名单文件解析失败（备份失败：磁盘已满，原文件原地保留，下次启动重试自愈）"),
        };
        var (vm, _, _, _) = ListPresentationTests.NewVm(whitelist: whitelist);

        Assert.Contains("原文件原地保留", vm.WhitelistNotice);
        Assert.DoesNotContain("已重置", vm.WhitelistNotice);
    }

    [Fact]
    public void 启动装载正常_无提示()
    {
        var (vm, _, _, _) = ListPresentationTests.NewVm();
        Assert.Null(vm.WhitelistNotice);
    }

    /// <summary>测试铺态用行构造（与 VM 投影同构；守卫测试不依赖投影细节）。</summary>
    private static ClassificationRow MakeRow(Classification c, ScanResult snapshot) => new(c, snapshot);
}
