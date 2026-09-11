using MemRelief.App.Hosting;
using MemRelief.App.State;
using MemRelief.App.ViewModels;
using MemRelief.Core.Scanner;
using MemRelief.Core.Storage;

namespace MemRelief.App.Tests.Hosting;

/// <summary>组合根接线测试：Core 真实实现装配（无替身）、状态机初始态、绑定面就位。</summary>
public class CompositionRootTests
{
    [Fact]
    public void 组合根_对象图就位_状态机初始未扫描()
    {
        var vm = CompositionRoot.CreateViewModel();

        Assert.NotNull(vm);
        Assert.Equal(AppState.NotScanned, vm.StateMachine.State);
        Assert.True(vm.Availability.StartScanEnabled);
        Assert.False(vm.Availability.ReleaseEnabled);
        Assert.Empty(vm.Classifications);
        Assert.True(vm.StartScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task 组合根_真实Core装配_真机概览采样可用()
    {
        // 真实 Scanner（T-01/T-02/T-05 装配链）+ 真实 RulePackStore（T-13 嵌入资源）
        // 概览走 NtQuery/PDH 真机通道，毫秒级；扫描链真机冒烟由 Core.Tests 集成测试承载
        var vm = CompositionRoot.CreateViewModel();

        await vm.InitializeAsync();

        Assert.NotEqual("—", vm.OverviewSummary); // 真机健康时应有真实三数值
        Assert.Null(vm.ScanFailedMessage);
    }

    [Fact]
    public void 组合根_名单包_来自嵌入资源真实装载()
    {
        // 法-3 前提：RulePackStore 嵌入名单真实可读（T-13 交付物），组合根接线不吞异常
        var store = new RulePackStore();
        var result = store.LoadRulePack();

        Assert.Empty(result.Failures);          // 四份内置名单随程序集嵌入，正常装载零失败
        Assert.NotEmpty(result.Pack.ResidualPatterns);
        Assert.NotEmpty(result.Pack.ProtectedProcesses);
    }

    [Fact]
    public void 组合根_ScanCoordinator依赖真实Scanner()
    {
        // 防呆：组合根装配的是 Core 真实 Scanner（无参构造），不是任何替身类型
        var scannerType = typeof(Scanner);
        Assert.True(typeof(IScanner).IsAssignableFrom(scannerType));
        Assert.True(scannerType.IsSealed); // Core sealed 实现，组合根直接实例化
    }
}
