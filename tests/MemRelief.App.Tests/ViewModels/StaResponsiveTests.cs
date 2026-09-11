using System.Windows.Threading;
using MemRelief.App.Scanning;
using MemRelief.App.State;
using MemRelief.App.Tests.TestDoubles;
using MemRelief.App.ViewModels;
using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using MemRelief.Core.Storage;

namespace MemRelief.App.Tests.ViewModels;

/// <summary>
/// AC-3 对抗性自测：扫描挂起期间 STA（UI 同构）线程消息泵保持分发——
/// 在真实 Dispatcher + DispatcherSynchronizationContext 上复现 WPF UI 线程行为：
/// 扫描发起于 STA 线程、采集通道挂起，此时 post 到该线程的消息必须被处理（窗口保持响应）。
/// </summary>
public class StaResponsiveTests
{
    [Fact]
    public async Task 扫描挂起期间_STA线程消息泵保持分发_扫描完成后窗口态更新()
    {
        var scanner = new FakeScanner();
        var gate = new TaskCompletionSource<ScanResult>();
        scanner.OnTakeSnapshot = async () =>
        {
            await gate.Task.ConfigureAwait(false);
            return FakeScanner.DefaultSnapshot;
        };
        var whitelist = new FakeWhitelistStore();
        var rules = new FakeRules();
        var coordinator = new ScanCoordinator(
            scanner, rules, new StaticRulePackStore(),
            new ClassificationContext(1, "u"), () => whitelist.Snapshot());
        var vm = new MainViewModel(new UiStateMachine(), coordinator, scanner, rules, whitelist);

        var staReady = new TaskCompletionSource();
        var probeRan = new TaskCompletionSource();
        Dispatcher? dispatcher = null;

        // IsBackground=true 兜底：任何断言失败路径下测试进程仍可退出，不因残留泵线程挂死门禁
        var sta = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                // 复现 WPF UI 线程上下文：await 延续回 STA 泵（同 DispatcherSynchronizationContext 语义）
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(dispatcher));
                staReady.SetResult();

                _ = vm.StartScanAsync(); // UI 线程发起扫描（真实形态）；采集挂起后同步段让出

                // 扫描仍挂起时投递探针：泵若冻结，探针永不执行
                dispatcher.BeginInvoke(new Action(() => probeRan.TrySetResult()), DispatcherPriority.Normal);
                Dispatcher.Run();
            }
            finally
            {
                probeRan.TrySetResult(); // 泵异常退出时解除主测试侧等待
            }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.IsBackground = true;
        sta.Start();
        await staReady.Task;

        // 探针在扫描挂起期间被分发 = UI 线程未阻塞
        var probeFinished = await Task.WhenAny(probeRan.Task, Task.Delay(5000));
        Assert.True(probeFinished == probeRan.Task, "扫描挂起期间 STA 线程消息泵未分发探针——UI 冻结复现");
        Assert.True(gate.Task.IsCompleted == false); // 探针分发时采集仍在进行
        Assert.Equal(AppState.Scanning, vm.StateMachine.State);

        // 放行采集：延续经 DispatcherSynchronizationContext 回 STA 线程完成状态更新
        gate.TrySetResult(FakeScanner.DefaultSnapshot);
        await WaitUntil.ForAsync(() => vm.StateMachine.State == AppState.ResultsShown, TimeSpan.FromSeconds(5));
        Assert.Equal(AppState.ResultsShown, vm.StateMachine.State);

        // 收尾：关泵、收线程（避免测试进程残留活动 Dispatcher）
        dispatcher!.Invoke(() => Dispatcher.ExitAllFrames(), DispatcherPriority.Send);
        sta.Join(5000);
        Assert.False(sta.IsAlive);
    }
}
