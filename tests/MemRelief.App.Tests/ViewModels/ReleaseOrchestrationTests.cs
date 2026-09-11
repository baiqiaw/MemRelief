using System.Windows.Input;
using MemRelief.App.Scanning;
using MemRelief.App.State;
using MemRelief.App.Tests.TestDoubles;
using MemRelief.App.ViewModels;
using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using MemRelief.Core.Storage;
using Xunit;

namespace MemRelief.App.Tests.ViewModels;

/// <summary>
/// 释放编排切片测试（T-10）：取消命令矩阵准入与转调、结果报告收口到状态机与绑定面。
/// Execute 触发/进度/日志追加接线归 T-16，不在本切片。
/// </summary>
public class ReleaseOrchestrationTests
{
    private static (MainViewModel Vm, FakeReleaser Releaser) NewVm(
        FakeReleaser? releaser = null, bool withReleaser = true)
    {
        releaser ??= new FakeReleaser();
        var scanner = new FakeScanner();
        var rules = new FakeRules();
        var whitelist = new FakeWhitelistStore();
        var coordinator = new ScanCoordinator(
            scanner, rules, new StaticRulePackStore(),
            new ClassificationContext(1, "u"), () => whitelist.Snapshot());
        return (new MainViewModel(
            new UiStateMachine(), coordinator, scanner, rules, whitelist,
            withReleaser ? releaser : null), releaser);
    }

    /// <summary>铺态至释放中（未扫描 → 扫描中 → 已展示 → 释放中，全走单一转换入口）。</summary>
    private static void ArrangeReleasing(MainViewModel vm)
    {
        Assert.True(vm.StateMachine.TryTransition(AppTrigger.StartScan));
        Assert.True(vm.StateMachine.TryTransition(AppTrigger.ScanCompleted));
        Assert.True(vm.StateMachine.TryTransition(AppTrigger.ReleaseConfirmed));
        Assert.Equal(AppState.Releasing, vm.StateMachine.State);
    }

    private static ReleaseReport Report() => new(
        Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow,
        Array.Empty<ReleaseItemResult>(),
        MainReleasedBytes: 60 * 1024 * 1024);

    // —— 取消命令：释放中可用且转调 releaser ——

    [Fact]
    public void 释放中_取消命令可用_执行转调Releaser()
    {
        var (vm, releaser) = NewVm();
        ArrangeReleasing(vm);

        Assert.True(vm.CancelReleaseCommand.CanExecute(null));
        vm.CancelReleaseCommand.Execute(null);

        Assert.Equal(1, releaser.CancelCalls);
    }

    [Fact]
    public void 释放中_重复取消_逐次转调_Releaser幂等由Core承担()
    {
        var (vm, releaser) = NewVm();
        ArrangeReleasing(vm);

        vm.CancelReleaseCommand.Execute(null);
        vm.CancelReleaseCommand.Execute(null);

        Assert.Equal(2, releaser.CancelCalls); // 转调透传；幂等语义归 Core Cancel
    }

    // —— 取消命令：非释放中态矩阵禁用 ——

    [Fact]
    public void 非释放中_取消命令不可用()
    {
        var (vm, _) = NewVm();
        Assert.False(vm.CancelReleaseCommand.CanExecute(null)); // 未扫描

        vm.StateMachine.TryTransition(AppTrigger.StartScan);
        Assert.False(vm.CancelReleaseCommand.CanExecute(null)); // 扫描中

        vm.StateMachine.TryTransition(AppTrigger.ScanCompleted);
        Assert.False(vm.CancelReleaseCommand.CanExecute(null)); // 已展示
    }

    [Fact]
    public void 未注入Releaser_释放中取消命令不可用_执行空安全()
    {
        var (vm, _) = NewVm(withReleaser: false);

        ArrangeReleasing(vm); // 矩阵 CancelReleaseEnabled=true

        Assert.False(vm.CancelReleaseCommand.CanExecute(null)); // releaser 未注入 → 不可用
        vm.CancelReleaseCommand.Execute(null); // 空安全：直接调用不抛
    }

    // —— 结果报告收口：存报告 + 状态转换（全部完成/取消同入口，PRD §3.6） ——

    [Fact]
    public void CompleteRelease_存报告_转入结果展示态()
    {
        var (vm, _) = NewVm();
        ArrangeReleasing(vm);
        var report = Report();

        vm.CompleteRelease(report);

        Assert.Equal(AppState.ReportShown, vm.StateMachine.State);
        Assert.Same(report, vm.LastReleaseReport);
        Assert.False(vm.Availability.CancelReleaseEnabled); // 报告态取消不可用（矩阵收口）
    }

    [Fact]
    public void CompleteRelease_取消收尾报告_同入口收口()
    {
        var (vm, releaser) = NewVm();
        ArrangeReleasing(vm);
        vm.CancelReleaseCommand.Execute(null);

        // 取消后 Execute 收尾照常产出报告（Core 口径），编排同入口收口
        vm.CompleteRelease(Report());

        Assert.Equal(AppState.ReportShown, vm.StateMachine.State);
        Assert.Equal(1, releaser.CancelCalls);
    }

    [Fact]
    public void CompleteRelease_非释放中态_状态机拒绝转换_报告仍留存()
    {
        var (vm, _) = NewVm(); // 未扫描态

        var report = Report();
        vm.CompleteRelease(report);

        Assert.Equal(AppState.NotScanned, vm.StateMachine.State); // 转换被拒：状态不变
        Assert.Same(report, vm.LastReleaseReport);
    }

    [Fact]
    public void CompleteRelease_null报告_拒绝()
    {
        var (vm, _) = NewVm();
        Assert.Throws<ArgumentNullException>(() => vm.CompleteRelease(null!));
    }
}
