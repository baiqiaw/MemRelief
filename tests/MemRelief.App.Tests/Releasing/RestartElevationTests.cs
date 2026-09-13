using System.ComponentModel;
using MemRelief.App.Hosting;
using MemRelief.App.Releasing;
using MemRelief.App.Scanning;
using MemRelief.App.State;
using MemRelief.App.Tests.TestDoubles;
using MemRelief.App.ViewModels;
using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using Xunit;

namespace MemRelief.App.Tests.Releasing;

/// <summary>
/// T-16 提权重启编排测试：入口可用性（预标项/报告失败项）、重启参数携带失败项清单、
/// UAC 拒绝停留普通权限、重启后失败项高亮不自动恢复勾选（PRD F3-6）。
/// </summary>
public class RestartElevationTests
{
    private static readonly DateTime TakenAt = new(2026, 9, 12, 8, 0, 0, DateTimeKind.Utc);

    private static ProcessSnapshot Proc(int pid, string name, long bytes) => new(
        pid, 0, name, $@"C:\apps\{name}", TakenAt.AddMinutes(-1), bytes);

    private static ScanResult Snapshot(params ProcessSnapshot[] procs) => new(
        TakenAt, procs.Length, 10, procs, []);

    private static Classification Classify(int pid, long treeBytes, bool requiresElevation = false) =>
        new(pid, Level.Recommend, [new Basis(1, "孤儿")], treeBytes, null, [], requiresElevation);

    private static (MainViewModel Vm, FakeReleaser Releaser, FakeRestarter Restarter,
        FakeShutdown Shutdown, FakeScanner Scanner, FakeRules Rules) NewVm(
        IReadOnlyList<RestartFailedItem>? restartFailedItems = null)
    {
        var scanner = new FakeScanner
        {
            OnTakeSnapshot = () => Task.FromResult(
                Snapshot(Proc(100, "a.exe", 60_000_000), Proc(200, "b.exe", 30_000_000))),
        };
        var rules = new FakeRules
        {
            Result =
            [
                Classify(100, 60_000_000),
                Classify(200, 30_000_000),
            ],
        };
        var whitelist = new FakeWhitelistStore();
        var releaser = new FakeReleaser();
        var restarter = new FakeRestarter();
        var shutdown = new FakeShutdown();
        var coordinator = new ScanCoordinator(
            scanner, rules, new StaticRulePackStore(),
            new ClassificationContext(1, "u"), () => whitelist.Snapshot());
        var vm = new MainViewModel(
            new UiStateMachine(), coordinator, rules, whitelist,
            releaser, null, new FakeConfirmDialog(), restarter, null, restartFailedItems,
            shutdown.AsAction());
        return (vm, releaser, restarter, shutdown, scanner, rules);
    }

    // —— 入口可用性：矩阵（ResultsShown/ReportShown）×内容条件（预标项/失败项） ——

    [Fact]
    public async Task 已展示态_含预标需管理员项_重启入口可用()
    {
        var (vm, _, _, _, _, rules) = NewVm();
        rules.Result = [Classify(100, 60_000_000, requiresElevation: true)];

        await vm.StartScanAsync();

        Assert.True(vm.CanRestartElevated);
    }

    [Fact]
    public async Task 已展示态_无预标项_重启入口不可用()
    {
        var (vm, _, _, _, _, rules) = NewVm();
        rules.Result = [Classify(100, 60_000_000)]; // 两行均无预标
        await vm.StartScanAsync();

        Assert.False(vm.CanRestartElevated);
    }

    [Fact]
    public void 未扫描态_重启入口不可用()
    {
        var (vm, _, _, _, _, _) = NewVm();
        Assert.False(vm.CanRestartElevated);
    }

    [Fact]
    public async Task 结果展示态_报告含需管理员失败项_重启入口可用()
    {
        var (vm, releaser, _, _, _, _) = NewVm();
        await vm.StartScanAsync();
        releaser.PlanResult =
        [
            new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000),
            new TreePlan(200, [new TreeNode(Proc(200, "b.exe", 30_000_000))], [], 30_000_000),
        ];
        releaser.OnExecute = (r, _) => Task.FromResult(new ReleaseReport(
            r.ReleaseId, r.RequestedAtUtc, DateTime.UtcNow, DateTime.UtcNow,
            [
                new ReleaseItemResult(100, "a.exe", null, null, ReleaseItemOutcome.Released),
                new ReleaseItemResult(200, "b.exe", null, null, ReleaseItemOutcome.NeedsElevation),
            ]));

        Assert.False(vm.CanRestartElevated); // 已展示态但列表无预标项（释放中不可用由矩阵测试钉死）

        await vm.ReleaseAsync();

        Assert.True(vm.CanRestartElevated); // 报告含 NeedsElevation 失败项
    }

    [Fact]
    public async Task 结果展示态_报告无失败项_重启入口不可用()
    {
        var (vm, releaser, _, _, _, _) = NewVm();
        await vm.StartScanAsync();
        releaser.PlanResult = [new TreePlan(100, [new TreeNode(Proc(100, "a.exe", 60_000_000))], [], 60_000_000)];
        releaser.OnExecute = (r, _) => Task.FromResult(new ReleaseReport(
            r.ReleaseId, r.RequestedAtUtc, DateTime.UtcNow, DateTime.UtcNow,
            [new ReleaseItemResult(100, "a.exe", null, null, ReleaseItemOutcome.Released)]));

        await vm.ReleaseAsync();

        Assert.False(vm.CanRestartElevated);
    }

    // —— 重启执行：清单携带/UAC 拒绝/关窗动作 ——

    [Fact]
    public async Task 重启执行_携带预标失败项清单_成功后触发关窗()
    {
        var (vm, _, restarter, shutdown, _, rules) = NewVm();
        rules.Result = [Classify(100, 60_000_000, requiresElevation: true)];
        await vm.StartScanAsync();

        vm.RestartElevatedCommand.Execute(null);

        Assert.Equal(1, restarter.RestartCalls);
        var items = restarter.LastFailedItems!;
        Assert.Single(items);
        Assert.Equal("a.exe", items[0].Name);
        Assert.Equal(@"C:\apps\a.exe", items[0].ExecutablePath);
        Assert.Equal(1, shutdown.Calls); // 新实例已拉起，本实例退出
    }

    [Fact]
    public async Task UAC拒绝_停留普通权限_不关窗_给提示()
    {
        var (vm, _, restarter, shutdown, _, rules) = NewVm();
        rules.Result = [Classify(100, 60_000_000, requiresElevation: true)];
        await vm.StartScanAsync();
        restarter.OnRestart = new Win32Exception(1223); // ERROR_CANCELLED：用户拒绝 UAC

        vm.RestartElevatedCommand.Execute(null);

        Assert.Equal(0, shutdown.Calls); // 停留普通权限
        Assert.NotNull(vm.WhitelistNotice);
    }

    [Fact]
    public async Task 重启其他失败_提示_不关窗()
    {
        var (vm, _, restarter, shutdown, _, rules) = NewVm();
        rules.Result = [Classify(100, 60_000_000, requiresElevation: true)];
        await vm.StartScanAsync();
        restarter.OnRestart = new InvalidOperationException("文件不存在");

        vm.RestartElevatedCommand.Execute(null);

        Assert.Equal(0, shutdown.Calls);
        Assert.NotNull(vm.WhitelistNotice);
    }

    // —— 重启后失败项高亮（不自动恢复勾选） ——

    [Fact]
    public async Task 重启后扫描_上次失败项高亮_不自动恢复勾选()
    {
        var (vm, _, _, _, _, _) = NewVm(
        [
            new RestartFailedItem("b.exe", @"C:\apps\b.exe"),
            new RestartFailedItem("ghost.exe", null), // 路径缺失：仅按名称匹配
        ]);

        await vm.StartScanAsync(); // 重启参数启动的自动扫描

        var rows = vm.Groups.SelectMany(g => g.Rows).ToList();
        var a = rows.Single(r => r.Pid == 100);
        var b = rows.Single(r => r.Pid == 200);
        Assert.True(b.IsHighlighted);  // 名称+路径双匹配
        Assert.False(a.IsHighlighted);
        // 高亮不自动恢复勾选：勾选默认态仅由级别决定（PRD F3-6“由用户重新勾选”）
        Assert.False(b.Level != Level.Recommend && b.IsChecked);
    }

    [Fact]
    public async Task 高亮匹配_名称同路径不同_不高亮()
    {
        var (vm, _, _, _, _, _) = NewVm([new RestartFailedItem("a.exe", @"D:\other\a.exe")]);

        await vm.StartScanAsync();

        Assert.DoesNotContain(vm.Groups.SelectMany(g => g.Rows), r => r.IsHighlighted);
    }

    [Fact]
    public async Task 无重启参数_扫描后无高亮()
    {
        var (vm, _, _, _, _, _) = NewVm();

        await vm.StartScanAsync();

        Assert.DoesNotContain(vm.Groups.SelectMany(g => g.Rows), r => r.IsHighlighted);
    }
}
