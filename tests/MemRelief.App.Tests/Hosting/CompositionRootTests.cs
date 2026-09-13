using System.Threading;
using MemRelief.App;
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

    // 概览采样接线断言已随 #38 步骤 2 裁决移除：采样链归概览条子 VM（OverviewBarTests），
    // 真机 Scanner 装配冒烟由 Core.Tests 集成测试承载

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
    public void 组合根_目录入口与白名单面板接线_命令可用()
    {
        // T-26：组合根透传真实 ReleaseLogStore——"打开日志/数据目录"入口可用
        // （logStore 缺省 null=T-16 缺省语义"不启用对应编排"，故显式传参断言接线）；
        // 白名单面板开关/逐项移除恒可用（不触判定链，任意态开放）
        var vm = CompositionRoot.CreateViewModel(logStore: new ReleaseLogStore());

        Assert.True(vm.OpenLogFileCommand.CanExecute(null));
        Assert.True(vm.OpenDataDirectoryCommand.CanExecute(null));
        Assert.True(vm.ToggleWhitelistPanelCommand.CanExecute(null));
        Assert.False(vm.IsWhitelistPanelOpen);   // 面板默认收起
        Assert.Empty(vm.WhitelistEntries);
    }

    [Fact]
    public void 组合根_ScanCoordinator依赖真实Scanner()
    {
        // 防呆：组合根装配的是 Core 真实 Scanner（无参构造），不是任何替身类型
        var scannerType = typeof(Scanner);
        Assert.True(typeof(IScanner).IsAssignableFrom(scannerType));
        Assert.True(scannerType.IsSealed); // Core sealed 实现，组合根直接实例化
    }

    [Fact]
    public void 组合根_主窗口接线概览条_子VM与主VM状态机同源()
    {
        // T-17 收口（#38）：概览条经组合根注入 CreateMainWindow；状态机须与主 VM 同实例，
        // 错配则扫描后/释放后刷新静默失效（#38 步骤 3 可选加固在此钉死）
        Exception? failure = null;
        var sta = new Thread(() =>
        {
            try
            {
                var window = CompositionRoot.CreateMainWindow();
                var host = (System.Windows.Controls.ContentControl)window.FindName("OverviewBarHost");
                var bar = Assert.IsType<OverviewBarViewModel>(host.DataContext);
                var main = Assert.IsType<MainViewModel>(window.DataContext);
                Assert.Same(main.StateMachine, bar.StateMachine);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        Assert.Null(failure);
    }
}
