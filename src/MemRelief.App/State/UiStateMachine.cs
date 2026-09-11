namespace MemRelief.App.State;

/// <summary>主界面五态（PRD §3.6，唯一状态权威）。</summary>
public enum AppState
{
    /// <summary>未扫描：应用启动后未产生数据。</summary>
    NotScanned,

    /// <summary>扫描中：快照采集与判定进行中（进行中态，控件统一禁用）。</summary>
    Scanning,

    /// <summary>已展示：三级列表可交互。</summary>
    ResultsShown,

    /// <summary>释放中：确认后执行释放（进行中态）。</summary>
    Releasing,

    /// <summary>结果展示：释放完成报告。</summary>
    ReportShown,
}

/// <summary>状态机触发（单一转换入口的入参；触发者=用户操作或编排结果）。</summary>
public enum AppTrigger
{
    /// <summary>点击“开始扫描”（用户）/管理员重启后自动触发（系统）。</summary>
    StartScan,

    /// <summary>扫描完成（编排成功收口）。</summary>
    ScanCompleted,

    /// <summary>扫描失败（编排失败收口；目标态按是否有历史结果分叉）。</summary>
    ScanFailed,

    /// <summary>确认弹窗确认释放（弹窗取消不产生触发，停留已展示态）。</summary>
    ReleaseConfirmed,

    /// <summary>释放结束：全部树完成或取消。</summary>
    ReleaseCompleted,

    /// <summary>关闭结果报告。</summary>
    ReportClosed,
}

/// <summary>控件可用性矩阵的一行快照（PRD §3.6 前端展示列的机器可读投影）。</summary>
public readonly record struct ControlAvailability(
    bool StartScanEnabled,
    bool ReleaseEnabled,
    bool ListInputEnabled,
    bool CancelReleaseEnabled,
    bool CloseReportEnabled);

/// <summary>
/// UI 状态机宿主（ui 模块，PRD §3.6 状态流转表为本类唯一状态权威）。
/// 契约（ui.md §4.2）：五态显式枚举 + <see cref="TryTransition"/> 单一转换入口；
/// 控件可用性由当前态派生（<see cref="Availability"/>），进行中态（扫描中/释放中）统一禁用
/// （PRD §3.6/§3.7“重复触发”行）；五态外的控件状态变更视为违规（ui.md §6 法条）。
/// 本类与 WPF 无关（纯 C#），可由 xUnit 全矩阵驱动。
/// </summary>
public sealed class UiStateMachine
{
    private AppState _state;

    public UiStateMachine() => _state = AppState.NotScanned;

    public AppState State => _state;

    /// <summary>
    /// 是否存在可保留的扫描结果（任一次扫描成功后恒真）。扫描失败按此分叉目标态：
    /// 首次（无结果）回未扫描，已有结果回已展示并保留旧结果（PRD §3.6 扫描中离开条件）。
    /// </summary>
    public bool HasResults { get; private set; }

    /// <summary>控件可用性矩阵（当前态派生，状态变化时随之更新并经 StateChanged 通知）。</summary>
    public ControlAvailability Availability => AvailabilityOf(_state);

    /// <summary>状态变化通知（仅真实变化时发出；参数=新状态）。UI 层据此刷新绑定。</summary>
    public event EventHandler<AppState>? StateChanged;

    /// <summary>
    /// 单一转换入口：查显式转换表，表内组合才生效；拒绝时状态不变、不发事件。
    /// 返回是否发生转换。
    /// </summary>
    public bool TryTransition(AppTrigger trigger)
    {
        var target = Resolve(trigger);
        if (target is null || target == _state)
        {
            return false;
        }

        _state = target.Value;
        if (trigger == AppTrigger.ScanCompleted)
        {
            HasResults = true;
        }

        StateChanged?.Invoke(this, _state);
        return true;
    }

    /// <summary>
    /// 转换表：PRD §3.6 全部合法（状态, 触发）→ 目标态组合，表外一律 null。
    /// ScanFailed 的分叉在 <see cref="Resolve"/> 内按 <see cref="HasResults"/> 展开。
    /// </summary>
    private AppState? Resolve(AppTrigger trigger) => trigger switch
    {
        // 点击“开始扫描”（用户）；管理员重启后启动自动触发（系统）同入口
        AppTrigger.StartScan => _state is AppState.NotScanned
            or AppState.ResultsShown
            or AppState.ReportShown
            ? AppState.Scanning
            : null,

        // 扫描完成 → 已展示
        AppTrigger.ScanCompleted => _state == AppState.Scanning ? AppState.ResultsShown : null,

        // 扫描失败：首次回未扫描，已有结果回已展示（保留旧结果，不展示半成品）
        AppTrigger.ScanFailed => _state == AppState.Scanning
            ? (HasResults ? AppState.ResultsShown : AppState.NotScanned)
            : null,

        // 确认弹窗确认 → 释放中（弹窗取消不产生触发）
        AppTrigger.ReleaseConfirmed => _state == AppState.ResultsShown ? AppState.Releasing : null,

        // 全部树完成/取消 → 结果展示
        AppTrigger.ReleaseCompleted => _state == AppState.Releasing ? AppState.ReportShown : null,

        // 关闭报告 → 已展示
        AppTrigger.ReportClosed => _state == AppState.ReportShown ? AppState.ResultsShown : null,

        _ => null,
    };

    /// <summary>
    /// 控件可用性矩阵（PRD §3.6 前端展示列）：
    /// 扫描中——开始扫描与一键释放均禁用，列表/搜索/右键只读置灰（保留上次结果并置灰）；
    /// 释放中——开始扫描、一键释放、勾选修改、右键均禁用，提供“取消”；
    /// 结果展示——报告为非模态面板，重扫可用；列表只读（离开条件仅关报告/重扫/提权重启，
    /// 一键释放不可达，勾选无出口，故禁用——PRD 未明文，保守禁用，待 T-15 列表交互落地时复核）。
    /// </summary>
    private static ControlAvailability AvailabilityOf(AppState state) => state switch
    {
        AppState.NotScanned => new ControlAvailability(
            StartScanEnabled: true, ReleaseEnabled: false, ListInputEnabled: false,
            CancelReleaseEnabled: false, CloseReportEnabled: false),
        AppState.Scanning => new ControlAvailability(
            StartScanEnabled: false, ReleaseEnabled: false, ListInputEnabled: false,
            CancelReleaseEnabled: false, CloseReportEnabled: false),
        AppState.ResultsShown => new ControlAvailability(
            StartScanEnabled: true, ReleaseEnabled: true, ListInputEnabled: true,
            CancelReleaseEnabled: false, CloseReportEnabled: false),
        AppState.Releasing => new ControlAvailability(
            StartScanEnabled: false, ReleaseEnabled: false, ListInputEnabled: false,
            CancelReleaseEnabled: true, CloseReportEnabled: false),
        AppState.ReportShown => new ControlAvailability(
            StartScanEnabled: true, ReleaseEnabled: false, ListInputEnabled: false,
            CancelReleaseEnabled: false, CloseReportEnabled: true),
        _ => throw new InvalidOperationException($"未知状态 {state}（五态外不可达）"),
    };
}
