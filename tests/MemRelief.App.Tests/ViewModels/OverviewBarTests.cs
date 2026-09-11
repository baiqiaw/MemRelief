using System.Windows;
using MemRelief.App;
using MemRelief.App.State;
using MemRelief.App.Text;
using MemRelief.App.ViewModels;
using MemRelief.App.Tests.TestDoubles;
using MemRelief.Core.Contracts;

namespace MemRelief.App.Tests.ViewModels;

/// <summary>
/// T-17 内存概览条（R05）AC 采证：三数值+口径说明文案、三时点刷新、读失败"—"不阻塞主流程。
/// 以 ViewModel/状态层测试实证（真机窗口视觉呈现归 T-21/T-22）；采样器用 FakeScanner 注入。
/// </summary>
public class OverviewBarTests
{
    private static readonly long Gb = 1024L * 1024 * 1024;

    /// <summary>健康概览夹具：16G 总量 / 8G 使用中（50%）/ 10G 提交 / 20G 上限 / 3G 备用。</summary>
    private static MemoryOverview Healthy { get; } = new(16 * Gb, 8 * Gb, 10 * Gb, 20 * Gb, 3 * Gb, MemoryOverviewSource.NtQuery);

    /// <summary>降级概览夹具（T-05 裁决②：Degraded ⇔ StandbyBytes=null）。</summary>
    private static MemoryOverview Degraded { get; } = new(16 * Gb, 8 * Gb, 10 * Gb, 20 * Gb, null, MemoryOverviewSource.Degraded);

    private static MemoryOverview FastAfter { get; } = new(16 * Gb, 6 * Gb, 9 * Gb, 20 * Gb, 2 * Gb, MemoryOverviewSource.Pdh);

    /// <summary>陈旧概览夹具（并发测试中晚完成的旧采样，数值与所有夹具可区分）。</summary>
    private static MemoryOverview Stale { get; } = new(16 * Gb, 15 * Gb, 15 * Gb, 20 * Gb, 99 * Gb, MemoryOverviewSource.NtQuery);

    [Fact]
    public async Task 启动时点_采样后三数值与口径说明文案就位()
    {
        var (vm, scanner, _) = Create(Healthy);

        await vm.InitializeAsync();

        Assert.Equal(1, scanner.SampleOverviewCount);
        Assert.Equal("8.0 GB", vm.InUseText);
        Assert.Equal("16.0 GB", vm.TotalText);
        Assert.Equal("50%", vm.InUsePercentText);
        Assert.Equal("10.0 GB", vm.CommitText);
        Assert.Equal("20.0 GB", vm.CommitLimitText);
        Assert.Equal("3.0 GB", vm.StandbyText);
        // 口径教育三要素（PRD F5）：占用率仅统计使用中、备用属可回收缓存、高占用未必是压力
        Assert.Contains("占用率", vm.NoteText);
        Assert.Contains("备用", vm.NoteText);
        Assert.Contains("未必是内存压力", vm.NoteText);
        Assert.False(vm.HasReadFailure);
        vm.Dispose();
    }

    [Fact]
    public void 初始未采样_三数值占位且不误报失败()
    {
        var (vm, _, _) = Create(Healthy);

        Assert.Equal("—", vm.InUseText);
        Assert.Equal("—", vm.CommitText);
        Assert.Equal("—", vm.StandbyText);
        Assert.Equal(string.Empty, vm.NoteText);
        Assert.False(vm.HasReadFailure);
    }

    [Fact]
    public async Task 三时点刷新_启动与扫描后与释放后各采样_关报告不采样()
    {
        var (vm, scanner, sm) = Create(Healthy);

        await vm.InitializeAsync(); // 时点 1：启动
        Assert.Equal(1, scanner.SampleOverviewCount);

        sm.TryTransition(AppTrigger.StartScan); // NotScanned→Scanning：非刷新时点
        await Task.Delay(100);
        Assert.Equal(1, scanner.SampleOverviewCount);

        sm.TryTransition(AppTrigger.ScanCompleted); // 时点 2：每次扫描后
        await WaitUntil.ForAsync(() => scanner.SampleOverviewCount == 2);
        Assert.Equal(2, scanner.SampleOverviewCount);

        sm.TryTransition(AppTrigger.ReleaseConfirmed); // ResultsShown→Releasing：非刷新时点
        await Task.Delay(100);
        Assert.Equal(2, scanner.SampleOverviewCount);

        sm.TryTransition(AppTrigger.ReleaseCompleted); // 时点 3：每次释放后（含取消收尾）
        await WaitUntil.ForAsync(() => scanner.SampleOverviewCount == 3);
        Assert.Equal(3, scanner.SampleOverviewCount);

        sm.TryTransition(AppTrigger.ReportClosed); // ReportShown→ResultsShown：非刷新时点
        await Task.Delay(100);
        Assert.Equal(3, scanner.SampleOverviewCount);
        vm.Dispose();
    }

    [Fact]
    public async Task 读失败_三数值降为占位并提示_不向调用方抛出()
    {
        var (vm, scanner, _) = Create(Healthy);
        scanner.OnSampleOverview = () => throw new InvalidOperationException("模拟计数器读取失败");

        await vm.InitializeAsync(); // 不抛异常（不阻塞主流程的机器判据）

        Assert.Equal("—", vm.InUseText);
        Assert.Equal("—", vm.CommitText);
        Assert.Equal("—", vm.StandbyText);
        Assert.Contains("内存数据暂不可用", vm.NoteText);
        Assert.True(vm.HasReadFailure);
    }

    [Fact]
    public async Task 读失败后_下次刷新自动重试并恢复()
    {
        var (vm, scanner, _) = Create(Healthy);
        var failed = false;
        scanner.OnSampleOverview = () =>
        {
            if (failed)
            {
                return Task.FromResult(Healthy);
            }

            failed = true;
            throw new InvalidOperationException("模拟计数器读取失败");
        };

        await vm.InitializeAsync();
        Assert.True(vm.HasReadFailure);

        await vm.RefreshAsync(); // PRD §3.7 恢复性：下次刷新自动重试

        Assert.Equal("8.0 GB", vm.InUseText);
        Assert.Equal("3.0 GB", vm.StandbyText);
        Assert.False(vm.HasReadFailure);
    }

    [Fact]
    public async Task 降级态_备用显示占位并注记降级_不算读失败()
    {
        var (vm, _, _) = Create(Degraded);

        await vm.InitializeAsync();

        Assert.Equal("8.0 GB", vm.InUseText);
        Assert.Equal("—", vm.StandbyText); // 两数值呈现（PRD F5 降级口径）
        Assert.Contains("降级", vm.NoteText);
        Assert.False(vm.HasReadFailure);
    }

    [Fact]
    public async Task 并发刷新_后发采样胜出_陈旧结果不回写()
    {
        var (vm, scanner, _) = Create(Healthy);
        var call = 0;
        var slow = new TaskCompletionSource<MemoryOverview>(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.OnSampleOverview = () => ++call == 1 ? slow.Task : Task.FromResult(FastAfter);

        var first = vm.RefreshAsync(); // 采样 1：慢
        await WaitUntil.ForAsync(() => scanner.SampleOverviewCount == 1);

        var second = vm.RefreshAsync(); // 采样 2：快，后发
        await second;
        Assert.Equal("6.0 GB", vm.InUseText); // 快结果已呈现

        slow.SetResult(Stale); // 陈旧采样此时才完成
        await first;
        Assert.Equal("6.0 GB", vm.InUseText); // 不被陈旧结果回写
        Assert.Equal("2.0 GB", vm.StandbyText);
    }

    [Fact]
    public async Task 采样异常不击穿状态迁移链_迁移正常完成()
    {
        var (vm, scanner, sm) = Create(Healthy);
        scanner.OnSampleOverview = () => throw new InvalidOperationException("模拟采样通道崩溃");

        sm.TryTransition(AppTrigger.StartScan);
        sm.TryTransition(AppTrigger.ScanCompleted); // 触发刷新（fire-and-forget），迁移链不得被采样异常打断

        Assert.Equal(AppState.ResultsShown, sm.State);
        await WaitUntil.ForAsync(() => scanner.SampleOverviewCount == 1);
    }

    [Fact]
    public async Task Dispose与在途失败采样并发_读失败态不回写()
    {
        var (vm, scanner, _) = Create(Healthy);
        var slow = new TaskCompletionSource<MemoryOverview>(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.OnSampleOverview = () => slow.Task;

        var first = vm.RefreshAsync();
        await WaitUntil.ForAsync(() => scanner.SampleOverviewCount == 1);
        vm.Dispose();
        slow.SetException(new InvalidOperationException("模拟采样失败"));
        await first; // 不外泄异常（fire-and-forget 路径防护）

        Assert.False(vm.HasReadFailure); // 读失败态不回写：Dispose 后空操作不变量全路径成立
        Assert.Equal("—", vm.InUseText);
    }

    [Fact]
    public async Task 订阅方异常不外泄_刷新任务正常完成()
    {
        var (vm, _, _) = Create(Healthy);
        vm.PropertyChanged += (_, _) => throw new InvalidOperationException("模拟订阅方异常");

        await vm.RefreshAsync(); // 不抛：投影阶段订阅方炸出不得成为未观察异常

        Assert.Equal("8.0 GB", vm.InUseText); // 字段先写后通知，投影本体已更新
    }

    [Fact]
    public async Task 契约不一致夹具_StandbyBytes与Source独立投影不崩()
    {
        // Degraded + 非空 standby（违反 T-05 裁决②的非法形态）：两字段独立投影，不崩不误报
        var (degradedVm, _, _) = Create(new MemoryOverview(16 * Gb, 8 * Gb, 10 * Gb, 20 * Gb, 3 * Gb, MemoryOverviewSource.Degraded));
        await degradedVm.InitializeAsync();
        Assert.Equal("3.0 GB", degradedVm.StandbyText);
        Assert.Contains("降级", degradedVm.NoteText);
        Assert.False(degradedVm.HasReadFailure);
        degradedVm.Dispose();

        // NtQuery + null standby（同属不一致形态）：备用位占位但不算读失败
        var (ntQueryVm, _, _) = Create(new MemoryOverview(16 * Gb, 8 * Gb, 10 * Gb, 20 * Gb, null, MemoryOverviewSource.NtQuery));
        await ntQueryVm.InitializeAsync();
        Assert.Equal("—", ntQueryVm.StandbyText);
        Assert.DoesNotContain("降级", ntQueryVm.NoteText);
        Assert.False(ntQueryVm.HasReadFailure);
        ntQueryVm.Dispose();
    }

    [Fact]
    public async Task Dispose后退订_状态迁移不再采样()
    {
        var (vm, scanner, sm) = Create(Healthy);
        await vm.InitializeAsync();
        Assert.Equal(1, scanner.SampleOverviewCount);

        vm.Dispose();
        vm.Dispose(); // 幂等
        sm.TryTransition(AppTrigger.StartScan);
        sm.TryTransition(AppTrigger.ScanCompleted);
        await vm.RefreshAsync(); // Dispose 后为空操作：不采样、不改绑定面

        Assert.Equal(1, scanner.SampleOverviewCount);
        Assert.Equal("8.0 GB", vm.InUseText);
    }

    [Fact]
    public async Task 极端值_零总量占用率钳0_大数值不崩溃()
    {
        var (vm, _, _) = Create(new MemoryOverview(0, 0, 0, 0, 0, MemoryOverviewSource.NtQuery));
        await vm.InitializeAsync();
        Assert.Equal("0%", vm.InUsePercentText);
        Assert.Equal("0.0 GB", vm.InUseText);
    }

    [Fact]
    public void 文案映射_GB换算一位小数_占用率四舍五入()
    {
        Assert.Equal("1.5 GB", OverviewText.Gb((long)(1.5 * Gb)));
        Assert.Equal("0.0 GB", OverviewText.Gb(0));
        Assert.Equal("33%", OverviewText.Percent(1 * Gb, 3 * Gb));
        Assert.Equal("13%", OverviewText.Percent(1 * Gb, 8 * Gb)); // 12.5% 中点四舍五入（AwayFromZero）
        Assert.Equal("100%", OverviewText.Percent(3 * Gb, 3 * Gb));
        Assert.Equal("0%", OverviewText.Percent(0, 0)); // 零总量防御：不产生 NaN
    }

    [Fact]
    public async Task 控件装载_XAML解析成功_未注入子VM时自折叠()
    {
        // STA 线程构造 WPF 控件（XAML InitializeComponent 运行时解析验证；视觉呈现仍归 T-21/T-22 真机）
        var visibility = await RunOnSta(() =>
        {
            var control = new OverviewBar();
            control.DataContext = new object(); // 非子 VM：组合根未接线形态
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            return control.Visibility;
        });

        Assert.Equal(Visibility.Collapsed, visibility);
    }

    [Fact]
    public async Task 控件装载_注入子VM时_Loaded驱动启动时点采样()
    {
        var (vm, scanner, _) = Create(Healthy);

        await RunOnSta<object?>(() =>
        {
            var bar = new OverviewBar { DataContext = vm };
            bar.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); // 同步触发 OnLoaded
            return null;
        });

        await WaitUntil.ForAsync(() => scanner.SampleOverviewCount == 1); // Loaded → 启动时点刷新
        Assert.Equal(1, scanner.SampleOverviewCount);
        vm.Dispose();
    }

    private static async Task<T> RunOnSta<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sta = new Thread(() =>
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        return await tcs.Task;
    }

    private static (OverviewBarViewModel Vm, FakeScanner Scanner, UiStateMachine StateMachine) Create(MemoryOverview overview)
    {
        var scanner = new FakeScanner();
        scanner.OnSampleOverview = () => Task.FromResult(overview);
        var stateMachine = new UiStateMachine();
        var vm = new OverviewBarViewModel(scanner, stateMachine);
        return (vm, scanner, stateMachine);
    }
}
