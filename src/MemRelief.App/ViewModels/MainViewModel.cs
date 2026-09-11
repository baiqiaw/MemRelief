using System.ComponentModel;
using System.Windows.Input;
using MemRelief.App.Scanning;
using MemRelief.App.State;
using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;

namespace MemRelief.App.ViewModels;

/// <summary>
/// 主窗口 ViewModel（ui 模块编排面）：持有五态状态机宿主，收口扫描链编排结果到绑定面。
/// 线程模型：StartScanAsync 由 UI 线程发起，await 延续回捕获的 UI 上下文后更新绑定属性
/// （await 上下文恢复即编组，ui.md §6 法条；本包不订阅 Core 后台线程事件，
/// releaser 事件接线归 T-16，届时经 Dispatcher 编组）。
/// 绑定面刷新策略：整组属性统一 RaiseAll（列表快照整体替换，无逐项高频更新）。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ScanCoordinator _coordinator;
    private readonly IScanner _overviewSampler;

    private IReadOnlyList<Classification> _classifications = [];
    private DateTime? _lastScanTakenAtUtc;
    private MemoryOverview? _overview;
    private string _overviewSummary = "—";
    private string? _scanFailedMessage;
    private string _statusText = string.Empty;

    public MainViewModel(UiStateMachine stateMachine, ScanCoordinator coordinator, IScanner overviewSampler)
    {
        StateMachine = stateMachine;
        _coordinator = coordinator;
        _overviewSampler = overviewSampler;
        StateMachine.StateChanged += (_, _) => OnStateChanged();
        StartScanCommand = new RelayCommand(
            () => _ = StartScanAsync(),
            () => StateMachine.Availability.StartScanEnabled);
        OnStateChanged();
    }

    /// <summary>
    /// 五态状态机宿主（测试铺态与 T-16 释放编排消费；View 只读绑定 State/Availability，
    /// 禁直调 TryTransition——转换入口唯一性归 ui.md 状态机法条）。
    /// </summary>
    public UiStateMachine StateMachine { get; }

    /// <summary>控件可用性矩阵（XAML 按属性路径绑定，如 Availability.StartScanEnabled）。</summary>
    public ControlAvailability Availability => StateMachine.Availability;

    /// <summary>三级列表数据源（快照整体替换；分组/勾选/展开归 T-15 落地）。</summary>
    public IReadOnlyList<Classification> Classifications
    {
        get => _classifications;
        private set => SetField(ref _classifications, value);
    }

    /// <summary>上次成功扫描的快照时间戳（PRD §3.6 已展示态“显示快照时间戳”）。</summary>
    public DateTime? LastScanTakenAtUtc
    {
        get => _lastScanTakenAtUtc;
        private set => SetField(ref _lastScanTakenAtUtc, value);
    }

    /// <summary>内存概览三数值（呈现细节与口径说明归 T-17）。</summary>
    public MemoryOverview? Overview
    {
        get => _overview;
        private set => SetField(ref _overview, value);
    }

    /// <summary>概览摘要文本；采样失败显示"—"（PRD §3.7“内存信息读取失败”行）。</summary>
    public string OverviewSummary
    {
        get => _overviewSummary;
        private set => SetField(ref _overviewSummary, value);
    }

    /// <summary>状态提示（空态引导/扫描中/快照时间戳/失败提示的正文区承载）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    /// <summary>扫描失败提示（"扫描失败：{原因}"，PRD §3.7；成功后清空）。</summary>
    public string? ScanFailedMessage
    {
        get => _scanFailedMessage;
        private set
        {
            if (SetField(ref _scanFailedMessage, value))
            {
                OnPropertyChanged(nameof(HasScanFailed));
            }
        }
    }

    /// <summary>失败提示可见性（XAML 布尔转换绑定用）。</summary>
    public bool HasScanFailed => _scanFailedMessage != null;

    public bool IsScanning => StateMachine.State == AppState.Scanning;

    public ICommand StartScanCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>启动时点初始化：概览刷新（PRD F5 三时点之一）。</summary>
    public async Task InitializeAsync()
    {
        await RefreshOverviewAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 开始扫描：状态机准入（拒绝=已在进行中，直接返回）→ 四步链整体挪离调用线程 →
    /// 成功更新结果，失败保留旧结果（AC-2）——失败路径不触碰 <see cref="Classifications"/>，
    /// 半成品无处进入绑定面。
    /// </summary>
    public async Task StartScanAsync()
    {
        try
        {
            await StartScanCoreAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 全路径兜底（准入通知段/收口段）：绑定订阅方异常不得滞留“扫描中”态或成为未观察异常。
            // 兜底自身的通知链（setter/TryTransition→StateChanged→RaiseAll）也可能被同一订阅方再次炸出：
            // 先置文案（ScanFailedMessage 通知不触发本测试形态的订阅方），再经防御壳回退状态——
            // TryTransition 先改状态后发通知，即使通知抛异常，状态回退也已生效；二次转换幂等被拒
            var message = $"扫描失败：{ex.GetType().Name}: {ex.Message}";
            try
            {
                ScanFailedMessage = message;
                StateMachine.TryTransition(AppTrigger.ScanFailed);
            }
            catch
            {
                StateMachine.TryTransition(AppTrigger.ScanFailed);
            }
        }
    }

    private async Task StartScanCoreAsync()
    {
        if (!StateMachine.TryTransition(AppTrigger.StartScan))
        {
            return; // 扫描中/释放中重复触发：状态机拒绝（PRD §3.7“重复触发”行）
        }

        ScanFailedMessage = null; // 准入即清上一轮失败提示，扫描期间不残留旧红字

        // PRD §3.4“扫描异步执行、UI 不冻结”：Core 采集器在首 await 前有同步枚举段
        // （原生枚举+四通道信号采集），必须整链挪离调用（UI）线程，不能依赖 async 方法隐式让出
        var run = await Task.Run(() => _coordinator.RunAsync()).ConfigureAwait(true);

        if (run.Success && run.Outcome != null)
        {
            Classifications = run.Outcome.Classifications;
            LastScanTakenAtUtc = run.Outcome.Snapshot.TakenAtUtc;
            ScanFailedMessage = null;
            StateMachine.TryTransition(AppTrigger.ScanCompleted);
            await RefreshOverviewAsync().ConfigureAwait(true);
        }
        else
        {
            // 保留旧结果：仅置失败提示与状态，不更新列表/时间戳
            ScanFailedMessage = $"扫描失败：{run.FailureReason}";
            StateMachine.TryTransition(AppTrigger.ScanFailed);
        }
    }

    private async Task RefreshOverviewAsync()
    {
        try
        {
            // 概览采样含同步 P/Invoke 通道梯（PDH 首次初始化可达百余 ms），同样不占调用线程
            Overview = await Task.Run(() => _overviewSampler.SampleOverview()).ConfigureAwait(true);
            OverviewSummary = FormatOverview(Overview);
        }
        catch (Exception)
        {
            // PRD §3.7“内存信息读取失败”：按无数据处理，显示“—”，不阻塞扫描与释放
            Overview = null;
            OverviewSummary = "—";
        }
    }

    /// <summary>概览摘要（骨架级：GB 一位小数；口径说明与三数值精排归 T-17）。</summary>
    private static string FormatOverview(MemoryOverview o)
    {
        static string Gb(long bytes) => (bytes / 1024.0 / 1024 / 1024).ToString("F1");
        return $"物理 {Gb(o.PhysicalTotalBytes)} GB · 使用中 {Gb(o.InUseBytes)} GB · 已提交 {Gb(o.CommitBytes)} GB";
    }

    /// <summary>状态提示文案（五态骨架映射；零推荐空态=PRD §3.7“扫描零推荐”行；释放/报告完整呈现归 T-16）。</summary>
    private void UpdateStatusText()
    {
        StatusText = StateMachine.State switch
        {
            AppState.NotScanned => "点击“开始扫描”检查可安全结束的残留进程",
            AppState.Scanning => "正在扫描…",
            AppState.ResultsShown when Classifications.Count == 0 => "当前无可释放的进程",
            AppState.ResultsShown when LastScanTakenAtUtc.HasValue =>
                $"快照时间：{LastScanTakenAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            AppState.Releasing => "正在释放…",
            AppState.ReportShown => "释放完成",
            _ => string.Empty,
        };
    }

    private void OnStateChanged()
    {
        UpdateStatusText();
        RaiseAll();
    }

    /// <summary>整组绑定属性通知（矩阵/状态/列表均为低频整体变化，统一刷新）。</summary>
    private void RaiseAll()
    {
        // StartScanCommand 的属性通知会触发 WPF 重新取绑定命令值并重查 CanExecute——
        // 仅靠 CommandManager.RequerySuggested 需等下一次输入/焦点事件，按钮置灰/恢复会滞后一拍
        OnPropertyChanged(nameof(StartScanCommand));
        OnPropertyChanged(nameof(Availability));
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(Classifications));
        OnPropertyChanged(nameof(LastScanTakenAtUtc));
        OnPropertyChanged(nameof(Overview));
        OnPropertyChanged(nameof(OverviewSummary));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ScanFailedMessage));
    }

    private bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name!);
        return true;
    }

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
