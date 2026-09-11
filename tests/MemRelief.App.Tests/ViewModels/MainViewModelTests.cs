
using System.Diagnostics;
using System.Windows.Input;
using MemRelief.App.Scanning;
using MemRelief.App.State;
using MemRelief.App.Tests.TestDoubles;
using MemRelief.App.ViewModels;
using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using MemRelief.Core.Storage;

namespace MemRelief.App.Tests.ViewModels;

/// <summary>
/// 主窗口 ViewModel 测试：扫描编排收口到状态机与绑定面。
/// AC-2 扫描失败保留旧结果不展示半成品；AC-3 扫描异步执行不阻塞调用线程（UI 不冻结的语义层）。
/// </summary>
public class MainViewModelTests
{
    private static MainViewModel NewVm(FakeScanner? scanner = null, FakeRules? rules = null)
    {
        scanner ??= new FakeScanner();
        rules ??= new FakeRules();
        var whitelist = new FakeWhitelistStore();
        var coordinator = new ScanCoordinator(
            scanner, rules, new StaticRulePackStore(),
            new ClassificationContext(1, "u"), () => whitelist.Snapshot());
        return new MainViewModel(new UiStateMachine(), coordinator, scanner, rules, whitelist);
    }

    // —— AC-3：异步执行，调用线程不等扫描 ——
    [Fact]
    public async Task 开始扫描_立即返回_调用线程不等待扫描完成()
    {
        var scanner = new FakeScanner();
        var gate = new TaskCompletionSource();
        scanner.OnTakeSnapshot = async () =>
        {
            await gate.Task; // 模拟慢速采集（≤3s 口径内的真实异步通道）
            return FakeScanner.DefaultSnapshot;
        };
        var vm = NewVm(scanner);

        var runTask = vm.StartScanAsync();

        // 调用已让出：状态先行进入扫描中、按钮禁用，而扫描尚未完成
        Assert.Equal(AppState.Scanning, vm.StateMachine.State);
        Assert.False(vm.Availability.StartScanEnabled);
        Assert.True(vm.IsScanning);
        Assert.False(runTask.IsCompleted);

        gate.SetResult();
        await runTask;

        Assert.Equal(AppState.ResultsShown, vm.StateMachine.State);
    }

    [Fact]
    public async Task 开始扫描_采集同步前缀不在调用线程执行()
    {
        // 真实 Scanner.TakeSnapshot 的原生枚举+四通道信号采集在首 await 之前同步执行；
        // 若该同步段落在 UI/调用线程，扫描期间窗口冻结（PRD §3.4 违规）
        var scanner = new FakeScanner();
        var gate = new TaskCompletionSource<ScanResult>();
        scanner.OnTakeSnapshot = () =>
        {
            Thread.Sleep(200); // 同步前缀（模拟采集段）
            return gate.Task;
        };
        var vm = NewVm(scanner);

        var stopwatch = Stopwatch.StartNew();
        var runTask = vm.StartScanAsync();
        stopwatch.Stop();

        Assert.Equal(AppState.Scanning, vm.StateMachine.State);
        Assert.True(stopwatch.ElapsedMilliseconds < 100,
            $"StartScanAsync 调用耗时 {stopwatch.ElapsedMilliseconds}ms——采集同步段阻塞了调用线程（UI 冻结）");

        gate.SetResult(FakeScanner.DefaultSnapshot);
        await runTask;
        Assert.Equal(AppState.ResultsShown, vm.StateMachine.State);
    }

    [Fact]
    public async Task 开始扫描_准入即清除上一轮失败提示()
    {
        var scanner = new FakeScanner();
        var vm = NewVm(scanner);

        await vm.StartScanAsync();
        scanner.OnTakeSnapshot = () => throw new InvalidOperationException("枚举整体失败");
        await vm.StartScanAsync();
        Assert.NotNull(vm.ScanFailedMessage);

        var gate = new TaskCompletionSource();
        scanner.OnTakeSnapshot = async () =>
        {
            await gate.Task;
            return FakeScanner.DefaultSnapshot;
        };
        var runTask = vm.StartScanAsync();
        // 重新扫描准入后、尚未完成时：上轮红字必须已清，用户不误读为本轮失败
        Assert.True(vm.IsScanning);
        Assert.Null(vm.ScanFailedMessage);

        gate.SetResult();
        await runTask;
        Assert.Null(vm.ScanFailedMessage);
    }

    [Fact]
    public async Task 扫描中再次开始扫描_被状态机拒绝_不重复扫描()
    {
        var scanner = new FakeScanner();
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        scanner.OnTakeSnapshot = async () =>
        {
            started.TrySetResult(); // 采集已实际开始（Task.Run 调度为异步，断言前须等此信号）
            await gate.Task;
            return FakeScanner.DefaultSnapshot;
        };
        var vm = NewVm(scanner);

        var first = vm.StartScanAsync();
        await started.Task;
        await vm.StartScanAsync(); // 扫描中重复触发：直接返回，不进入第二次扫描
        Assert.Equal(1, scanner.TakeSnapshotCount);

        gate.SetResult();
        await first;
        Assert.Equal(1, scanner.TakeSnapshotCount);
    }

    // —— 扫描成功：结果与时间戳就位 ——
    [Fact]
    public async Task 扫描成功_列表与快照时间戳更新_转已展示()
    {
        var vm = NewVm();

        await vm.StartScanAsync();

        Assert.Equal(AppState.ResultsShown, vm.StateMachine.State);
        Assert.Single(vm.Classifications);
        Assert.Equal(FakeScanner.DefaultSnapshot.TakenAtUtc, vm.LastScanTakenAtUtc);
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task 扫描成功_概览随扫描后时点刷新()
    {
        var scanner = new FakeScanner();
        var vm = NewVm(scanner);

        await vm.InitializeAsync(); // 启动时点
        Assert.Equal(1, scanner.SampleOverviewCount);

        await vm.StartScanAsync(); // 扫描后时点
        Assert.Equal(2, scanner.SampleOverviewCount);
        Assert.NotNull(vm.Overview);
    }

    [Fact]
    public async Task 概览采样失败_显示短横线_不阻塞扫描()
    {
        var scanner = new FakeScanner
        {
            OnSampleOverview = () => throw new InvalidOperationException("系统接口不可用"),
        };
        var vm = NewVm(scanner);

        await vm.InitializeAsync(); // 不抛

        Assert.Equal("—", vm.OverviewSummary);
        Assert.Null(vm.Overview);

        await vm.StartScanAsync(); // 主流程不受概览影响
        Assert.Equal(AppState.ResultsShown, vm.StateMachine.State);
        Assert.Equal("—", vm.OverviewSummary);
    }

    [Fact]
    public async Task 扫描零推荐_正常空态_非错误()
    {
        var rules = new FakeRules { Result = [] };
        var vm = NewVm(rules: rules);

        await vm.StartScanAsync();

        Assert.Equal(AppState.ResultsShown, vm.StateMachine.State);
        Assert.Empty(vm.Classifications);
        Assert.Null(vm.ScanFailedMessage); // 与扫描失败严格区分：无错误提示
    }

    // —— AC-2：扫描失败保留旧结果，不展示半成品 ——
    [Fact]
    public async Task 扫描失败_已有结果_保留旧结果并回已展示()
    {
        var scanner = new FakeScanner();
        var vm = NewVm(scanner);

        await vm.StartScanAsync(); // 首次成功 → 列表有旧结果
        var oldRows = vm.Classifications.ToList();
        Assert.Single(oldRows);

        scanner.OnTakeSnapshot = () => throw new InvalidOperationException("枚举整体失败");

        await vm.StartScanAsync(); // 第二次失败

        // 旧结果原样保留（半成品无处展示：列表引用未换、内容未动）
        Assert.Equal(oldRows, vm.Classifications.ToList());
        Assert.Single(vm.Classifications);
        Assert.Equal(AppState.ResultsShown, vm.StateMachine.State);
        Assert.NotNull(vm.ScanFailedMessage);
        Assert.Contains("枚举整体失败", vm.ScanFailedMessage);
    }

    [Fact]
    public async Task 扫描失败_首次无结果_回未扫描且列表空()
    {
        var scanner = new FakeScanner
        {
            OnTakeSnapshot = () => throw new InvalidOperationException("枚举整体失败"),
        };
        var vm = NewVm(scanner);

        await vm.StartScanAsync();

        Assert.Equal(AppState.NotScanned, vm.StateMachine.State);
        Assert.Empty(vm.Classifications);
        Assert.Contains("枚举整体失败", vm.ScanFailedMessage);
    }

    [Fact]
    public async Task 失败后再成功_旧失败提示清除_新结果就位()
    {
        var scanner = new FakeScanner();
        var vm = NewVm(scanner);

        await vm.StartScanAsync();
        scanner.OnTakeSnapshot = () => throw new InvalidOperationException("枚举整体失败");
        await vm.StartScanAsync();
        Assert.NotNull(vm.ScanFailedMessage);

        scanner.OnTakeSnapshot = () => Task.FromResult(FakeScanner.DefaultSnapshot);
        await vm.StartScanAsync();

        Assert.Null(vm.ScanFailedMessage);
        Assert.Equal(FakeScanner.DefaultSnapshot.TakenAtUtc, vm.LastScanTakenAtUtc);
    }

    [Fact]
    public async Task 收口段异常_兜底到失败态_不滞留扫描中()
    {
        // 对抗性：绑定订阅方在结果赋值点抛异常（WPF 绑定转换异常的近似形态）
        var vm = NewVm();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Classifications))
            {
                throw new InvalidOperationException("订阅方异常");
            }
        };

        await vm.StartScanAsync(); // 不向上抛（fire-and-forget 链路无未观察异常）

        Assert.Equal(AppState.NotScanned, vm.StateMachine.State); // 首扫无结果：失败回未扫描
        Assert.NotNull(vm.ScanFailedMessage);
        Assert.Contains("订阅方异常", vm.ScanFailedMessage);
        Assert.False(vm.IsScanning);
    }

    // —— 命令可用性跟随状态机矩阵 ——
    [Fact]
    public void 开始扫描命令_可用性跟随状态机矩阵()
    {
        var vm = NewVm();
        Assert.True(vm.StartScanCommand.CanExecute(null)); // 未扫描

        vm.StateMachine.TryTransition(AppTrigger.StartScan);
        Assert.False(vm.StartScanCommand.CanExecute(null)); // 扫描中禁用（进行中态矩阵）
    }
}
