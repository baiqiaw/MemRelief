using System.ComponentModel;
using MemRelief.App.State;
using MemRelief.App.Text;
using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;

namespace MemRelief.App.ViewModels;

/// <summary>
/// 内存概览条子视图模型（R05/T-17，独立于 MainViewModel 的并行车道隔离承载）：
/// 自持采样（IScanner.SampleOverview），三时点刷新（PRD F5）——启动（<see cref="InitializeAsync"/>，
/// 由控件 Loaded 驱动）、每次扫描后（状态边沿 Scanning→ResultsShown）、每次释放后
/// （Releasing→ReportShown，含取消收尾）；边沿识别订阅 UiStateMachine（唯一状态权威，
/// 不依赖 MainViewModel，组合根接线按需注入同实例）。
/// 读失败按 PRD §3.7“内存信息读取失败”行：三数值显示“—”并提示，不阻塞主流程，下次刷新自动重试。
/// 线程模型：触发点均在 UI 线程（状态迁移/Launch），采样整段挪离调用线程（Task.Run），
/// await 延续回捕获上下文即编组（ui.md §6 法条同 MainViewModel 口径）。
/// 代际守卫：后发采样胜出，慢完成的陈旧结果不回写绑定面。
/// </summary>
public sealed class OverviewBarViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IScanner _sampler;
    private readonly UiStateMachine _stateMachine;
    private AppState _observedState;
    private int _generation;
    private bool _disposed;

    private string _inUseText = OverviewText.Placeholder;
    private string _totalText = OverviewText.Placeholder;
    private string _inUsePercentText = OverviewText.Placeholder;
    private string _commitText = OverviewText.Placeholder;
    private string _commitLimitText = OverviewText.Placeholder;
    private string _standbyText = OverviewText.Placeholder;
    private string _noteText = string.Empty;
    private bool _hasReadFailure;

    public OverviewBarViewModel(IScanner overviewSampler, UiStateMachine stateMachine)
    {
        _sampler = overviewSampler;
        _stateMachine = stateMachine;
        _observedState = stateMachine.State;
        stateMachine.StateChanged += OnStateChanged;
    }

    /// <summary>启动时点刷新（PRD F5 三时点之一）；幂等只读，可重复调用。</summary>
    public Task InitializeAsync() => RefreshAsync();

    /// <summary>
    /// 采样并投影三数值与文案。采样异常收口为读失败态（不向调用方抛出）；
    /// 代际守卫拒绝陈旧结果回写；Dispose 后为空操作（订阅已释放）。
    /// 本方法也被 fire-and-forget 路径（控件 Loaded/状态边沿）调用：外层收口兜住全部异常
    /// （含绑定订阅方炸出），禁未观察异常外泄（MainViewModel.StartScanAsync 防御壳同口径）。
    /// </summary>
    public async Task RefreshAsync()
    {
        try
        {
            await RefreshCoreAsync().ConfigureAwait(true);
        }
        catch
        {
            // 内层已收口采样失败与代际/Dispose 竞态；此处兜投影阶段订阅方异常，绑定面保持现状
        }
    }

    private async Task RefreshCoreAsync()
    {
        if (_disposed)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _generation);
        MemoryOverview overview;
        try
        {
            overview = await Task.Run(_sampler.SampleOverview).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // PRD §3.7：按无数据处理，不阻塞扫描与释放；恢复性=下次刷新自动重试。
            // 代际+Dispose 双守卫：Dispose 与在途失败采样并发时同样不回写（空操作不变量全路径成立）
            if (_disposed || generation != Volatile.Read(ref _generation))
            {
                return;
            }

            ApplyReadFailure();
            return;
        }

        if (_disposed || generation != Volatile.Read(ref _generation))
        {
            return; // 后发采样已接管（或已释放）：陈旧结果不回写
        }

        Apply(overview);
    }

    /// <summary>退订状态机（订阅随窗口生命周期释放，ui.md §6 法条）；幂等。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stateMachine.StateChanged -= OnStateChanged;
    }

    private void OnStateChanged(object? sender, AppState newState)
    {
        if (_disposed)
        {
            return;
        }

        var previous = _observedState;
        _observedState = newState;
        var afterScan = previous == AppState.Scanning && newState == AppState.ResultsShown;
        var afterRelease = previous == AppState.Releasing && newState == AppState.ReportShown;
        if (afterScan || afterRelease)
        {
            _ = RefreshAsync(); // 状态迁移链上的旁路刷新：异常已在 RefreshAsync 内收口，不击穿迁移
        }
    }

    private void Apply(MemoryOverview o)
    {
        InUseText = OverviewText.Gb(o.InUseBytes);
        TotalText = OverviewText.Gb(o.PhysicalTotalBytes);
        InUsePercentText = OverviewText.Percent(o.InUseBytes, o.PhysicalTotalBytes);
        CommitText = OverviewText.Gb(o.CommitBytes);
        CommitLimitText = OverviewText.Gb(o.CommitLimitBytes);
        // 降级语义（T-05 裁决②：Degraded ⇔ StandbyBytes=null）：备用位显示“—”并注记
        StandbyText = o.StandbyBytes is long standby ? OverviewText.Gb(standby) : OverviewText.Placeholder;
        NoteText = o.Source == MemoryOverviewSource.Degraded ? OverviewText.DegradedNote : OverviewText.CaliberNote;
        HasReadFailure = false;
    }

    private void ApplyReadFailure()
    {
        InUseText = OverviewText.Placeholder;
        TotalText = OverviewText.Placeholder;
        InUsePercentText = OverviewText.Placeholder;
        CommitText = OverviewText.Placeholder;
        CommitLimitText = OverviewText.Placeholder;
        StandbyText = OverviewText.Placeholder;
        NoteText = OverviewText.ReadFailedNote;
        HasReadFailure = true;
    }

    public string InUseText
    {
        get => _inUseText;
        private set => SetField(ref _inUseText, value);
    }

    public string TotalText
    {
        get => _totalText;
        private set => SetField(ref _totalText, value);
    }

    /// <summary>占用率（InUse / 物理总量）；口径=仅使用中内存，备用缓存不计入（PRD F5）。</summary>
    public string InUsePercentText
    {
        get => _inUsePercentText;
        private set => SetField(ref _inUsePercentText, value);
    }

    public string CommitText
    {
        get => _commitText;
        private set => SetField(ref _commitText, value);
    }

    public string CommitLimitText
    {
        get => _commitLimitText;
        private set => SetField(ref _commitLimitText, value);
    }

    public string StandbyText
    {
        get => _standbyText;
        private set => SetField(ref _standbyText, value);
    }

    /// <summary>口径说明/降级注记/读失败提示（同一文案位按状态切换）。</summary>
    public string NoteText
    {
        get => _noteText;
        private set => SetField(ref _noteText, value);
    }

    /// <summary>读失败标记（XAML 据此切换提示色；降级不算读失败）。</summary>
    public bool HasReadFailure
    {
        get => _hasReadFailure;
        private set => SetField(ref _hasReadFailure, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        return true;
    }
}
