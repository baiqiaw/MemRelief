using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using MemRelief.App.Hosting;
using MemRelief.App.Releasing;
using MemRelief.App.Scanning;
using MemRelief.App.State;
using MemRelief.App.Text;
using MemRelief.Core.Contracts;
using MemRelief.Core.Releaser;
using MemRelief.Core.Rules;
using MemRelief.Core.Storage;

namespace MemRelief.App.ViewModels;

/// <summary>
/// 主窗口 ViewModel（ui 模块编排面）：持有五态状态机宿主，收口扫描链编排结果到绑定面。
/// 线程模型：StartScanAsync/ReleaseAsync 由 UI 线程发起，await 延续回捕获的 UI 上下文后更新绑定属性
/// （await 上下文恢复即编组，ui.md §6 法条）。releaser 后台线程事件经构造注入的 <paramref name="marshal"/>
/// 编组到 UI 线程后收口（生产=Dispatcher.BeginInvoke；订阅随窗口生命周期=应用生命周期一致，无独立退订点）。
/// 绑定面刷新策略：整组属性统一 RaiseAll（列表快照整体替换，无逐项高频更新）。
/// 三级列表（T-15）：分组投影 <see cref="Groups"/>、搜索框全量查询（IRulesEngine.Query）、
/// 右键加白即时重判（③.s4 裁决⑥）、白名单排除计数。
/// 释放交互闭环（T-16）：确认弹窗（N 树/X MB）→ Execute+树级进度+取消 → 结果报告收口
/// （双释放量/跳过说明/被拉起提示/日志追加单路径）→ 已结束项移除+"可重新扫描"；
/// 释放中关窗=取消未开始树并等待收尾（<see cref="PrepareClose"/>/<see cref="SettleReleaseAsync"/>）；
/// 提权重启编排（入口+失败项清单+UAC 拒绝停留）与重启链失败项高亮（PRD F3-6）。
/// 概览采样链已随 #38 步骤 2 裁决删除（概览条子 VM 独立采样，OverviewText 唯一格式化实现）。
/// 白名单管理面板（T-26，F4）：条目元数据列表+逐项移除（移除后重扫恢复参与判定，不即时重判）、
/// 折叠区排除项清单投影、"打开日志/数据目录"入口（F6）。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ScanCoordinator _coordinator;
    private readonly IRulesEngine _rules;
    private readonly IWhitelistStore _whitelistStore;
    private readonly IReleaser? _releaser;
    private readonly IReleaseLogStore? _logStore;
    private readonly IReleaseConfirmDialog? _confirmDialog;
    private readonly IAppRestarter? _restarter;
    private readonly Action<Action> _marshal;
    private readonly IReadOnlyList<RestartFailedItem>? _restartFailedItems;
    private readonly Action? _shutdown;
    private readonly Action<string> _shellOpen;

    private IReadOnlyList<Classification> _classifications = [];
    private ScanResult? _snapshot;
    private IReadOnlyList<LevelGroup> _groups = [];
    private DateTime? _lastScanTakenAtUtc;
    private string? _scanFailedMessage;
    private string _statusText = string.Empty;
    private string _searchText = string.Empty;
    private IReadOnlyList<SearchResultRow> _searchResults = [];
    private string _searchStatusText = string.Empty;
    private int _searchSeq;
    private string? _whitelistNotice;
    private ReleaseReport? _lastReleaseReport;
    private string? _releaseFailedMessage;
    private bool _isWhitelistPanelOpen;
    private IReadOnlyList<WhitelistEntryRow> _whitelistEntries = [];
    private IReadOnlyList<WhitelistedExcludedRow> _whitelistedExcludedRows = [];

    // 释放编排在途状态（一次释放一个生命周期；字段仅在 UI 线程读写——事件经 _marshal 编组后触碰）
    private Task<ReleaseReport>? _releaseTask;
    private Task<ReleaseLogAppendResult>? _logTask;
    private int _totalTrees;
    private readonly HashSet<int> _finishedTrees = [];
    private IReadOnlyDictionary<int, Classification> _releaseContext =
        new Dictionary<int, Classification>();
    private readonly HashSet<Guid> _loggedReleaseIds = [];
    private string? _logError;
    private bool _cancelRequested;

    /// <summary>
    /// releaser 可空注入（T-16 起生产组合根恒注入；未注入时释放/取消命令不可用，矩阵+空参双保险）。
    /// 日志存储/确认弹窗/重启器可空注入（null=对应编排不启用，LogPersisted 恒 null=未尝试）。
    /// marshal：后台线程事件到 UI 线程的编组通道（null=同步直调，测试便利）。restartFailedItems：
    /// T-27 重启参数解析出的上次失败项清单（名称+可执行路径，不落盘），扫描收口后高亮不自动勾选。
    /// shellOpen 可注入（T-26 目录入口）：shell 打开动作单点，默认 UseShellExecute；测试注入记录委托。
    /// </summary>
    public MainViewModel(
        UiStateMachine stateMachine,
        ScanCoordinator coordinator,
        IRulesEngine rules,
        IWhitelistStore whitelistStore,
        IReleaser? releaser = null,
        IReleaseLogStore? logStore = null,
        IReleaseConfirmDialog? confirmDialog = null,
        IAppRestarter? restarter = null,
        Action<Action>? marshal = null,
        IReadOnlyList<RestartFailedItem>? restartFailedItems = null,
        Action? shutdown = null,
        Action<string>? shellOpen = null)
    {
        StateMachine = stateMachine;
        _coordinator = coordinator;
        _rules = rules;
        _whitelistStore = whitelistStore;
        _releaser = releaser;
        _logStore = logStore;
        _confirmDialog = confirmDialog;
        _restarter = restarter;
        _restartFailedItems = restartFailedItems;
        _shutdown = shutdown;
        _marshal = marshal ?? (action => action());
        _shellOpen = shellOpen ?? DefaultShellOpen;
        StateMachine.StateChanged += (_, _) => OnStateChanged();
        StartScanCommand = new RelayCommand(
            () => _ = StartScanAsync(),
            () => StateMachine.Availability.StartScanEnabled);
        SearchCommand = new RelayCommand(
            () => _ = SearchAsync(),
            () => StateMachine.Availability.ListInputEnabled);
        WhitelistCommand = new RelayCommand<object>(
            o => _ = WhitelistAsync(o as ClassificationRow),
            o => o is ClassificationRow { CanWhitelist: true } && StateMachine.Availability.ListInputEnabled);
        CancelReleaseCommand = new RelayCommand(
            RequestCancel,
            () => _releaser is not null && StateMachine.Availability.CancelReleaseEnabled);
        ReleaseCommand = new RelayCommand(
            () => _ = ReleaseAsync(),
            () => _releaser is not null && StateMachine.Availability.ReleaseEnabled);
        CloseReportCommand = new RelayCommand(
            () => StateMachine.TryTransition(AppTrigger.ReportClosed),
            () => StateMachine.Availability.CloseReportEnabled);
        RestartElevatedCommand = new RelayCommand(
            RestartElevated,
            () => CanRestartElevated && _restarter is not null);
        ToggleWhitelistPanelCommand = new RelayCommand(ToggleWhitelistPanel);
        RemoveWhitelistEntryCommand = new RelayCommand<object>(
            o => _ = RemoveWhitelistEntryAsync(o as WhitelistEntryRow),
            o => o is WhitelistEntryRow);
        OpenLogFileCommand = new RelayCommand(OpenLogFile, () => _logStore is not null);
        OpenDataDirectoryCommand = new RelayCommand(OpenDataDirectory, () => _logStore is not null);
        // 启动装载即感知白名单损坏自愈（IWhitelistStore.Recovery 唯一通道，storage.md §4.1）；
        // 提示携带 Recovery.Reason：备份失败变体（原文件原地保留）与已重置变体的事实不同，禁固定文案掩盖差异
        if (whitelistStore.Recovery is not null)
        {
            WhitelistNotice = $"白名单异常已处理：{whitelistStore.Recovery.Reason}";
        }

        if (_releaser is not null)
        {
            // Core 后台线程事件 → UI 线程编组（data-contracts §1.5 线程亲和性；ui.md §6 法条）。
            // 订阅随窗口生命周期释放：releaser 与 VM 同由组合根持有、同生命周期，无独立退订点
            _releaser.TreeProgress += (rootPid, state) =>
                _marshal(() => OnTreeProgress(rootPid, state));
            _releaser.ReleaseCompleted += report =>
                _marshal(() => CompleteRelease(report));
        }

        OnStateChanged();
    }

    /// <summary>
    /// 五态状态机宿主（测试铺态与释放编排消费；View 只读绑定 State/Availability，
    /// 禁直调 TryTransition——转换入口唯一性归 ui.md 状态机法条）。
    /// </summary>
    public UiStateMachine StateMachine { get; }

    /// <summary>控件可用性矩阵（XAML 按属性路径绑定，如 Availability.StartScanEnabled）。</summary>
    public ControlAvailability Availability => StateMachine.Availability;

    /// <summary>三级列表数据源（快照整体替换）。</summary>
    public IReadOnlyList<Classification> Classifications
    {
        get => _classifications;
        private set => SetField(ref _classifications, value);
    }

    /// <summary>三级分组投影（✅/⚠️/🚫 各一组，白名单/未命中项不在组内；空扫描=空集）。</summary>
    public IReadOnlyList<LevelGroup> Groups
    {
        get => _groups;
        private set => SetField(ref _groups, value);
    }

    /// <summary>因白名单排除的进程数（F2 列表尾部计数；加白即时同步，③.s4 裁决⑥）。</summary>
    public int WhitelistedExcludedCount { get; private set; }

    /// <summary>折叠区排除项清单（F2"因白名单排除 N 项"展开可见，消除列表外黑箱）。</summary>
    public IReadOnlyList<WhitelistedExcludedRow> WhitelistedExcludedRows
    {
        get => _whitelistedExcludedRows;
        private set => SetField(ref _whitelistedExcludedRows, value);
    }

    /// <summary>上次成功扫描的快照时间戳（PRD §3.6 已展示态“显示快照时间戳”）。</summary>
    public DateTime? LastScanTakenAtUtc
    {
        get => _lastScanTakenAtUtc;
        private set => SetField(ref _lastScanTakenAtUtc, value);
    }

    /// <summary>状态提示（空态引导/扫描中/快照时间戳/失败提示/释放进度/报告可重扫的正文区承载）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    /// <summary>扫描失败提示（“扫描失败：{原因}”，PRD §3.7；成功后清空）。</summary>
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

    /// <summary>释放编排失败提示（规划失败/执行链违约兜底；成功链恒空，不与报告呈现混用）。</summary>
    public string? ReleaseFailedMessage
    {
        get => _releaseFailedMessage;
        private set
        {
            if (SetField(ref _releaseFailedMessage, value))
            {
                OnPropertyChanged(nameof(HasReleaseFailed));
            }
        }
    }

    /// <summary>释放失败提示可见性（XAML 布尔转换绑定用）。</summary>
    public bool HasReleaseFailed => _releaseFailedMessage != null;

    /// <summary>轻提示（白名单自愈/加白与移除失败/面板刷新失败/目录入口打开失败/提权重启编排反馈；
    /// null=无提示，同一展示位）。</summary>
    public string? WhitelistNotice
    {
        get => _whitelistNotice;
        private set
        {
            if (SetField(ref _whitelistNotice, value))
            {
                OnPropertyChanged(nameof(HasWhitelistNotice));
            }
        }
    }

    /// <summary>轻提示可见性（XAML 布尔转换绑定用）。</summary>
    public bool HasWhitelistNotice => _whitelistNotice != null;

    /// <summary>白名单管理面板展开态（工具栏入口开关；非状态机态——面板不触判定链，任意五态可用）。</summary>
    public bool IsWhitelistPanelOpen
    {
        get => _isWhitelistPanelOpen;
        private set => SetField(ref _isWhitelistPanelOpen, value);
    }

    /// <summary>白名单面板条目行（存储全量条目的只读投影；每次打开/移除后整体替换）。</summary>
    public IReadOnlyList<WhitelistEntryRow> WhitelistEntries
    {
        get => _whitelistEntries;
        private set
        {
            if (SetField(ref _whitelistEntries, value))
            {
                OnPropertyChanged(nameof(HasWhitelistEntries));
            }
        }
    }

    /// <summary>面板非空标记（XAML 空态文案切换绑定用）。</summary>
    public bool HasWhitelistEntries => _whitelistEntries.Count > 0;

    /// <summary>最近一次释放结果报告（呈现经 <see cref="ReleaseReportText"/>；回填 LogPersisted 时换实例）。</summary>
    public ReleaseReport? LastReleaseReport
    {
        get => _lastReleaseReport;
        private set => SetField(ref _lastReleaseReport, value);
    }

    /// <summary>释放结果报告全文（完成/取消头行/双释放量/逐项/被拉起提示/日志结果，映射单点 DisplayText；报告态绑定面）。</summary>
    public string ReleaseReportText => LastReleaseReport is null
        ? string.Empty
        : DisplayText.ReleaseReportSummary(LastReleaseReport, _releaseContext, _logError, _cancelRequested);

    /// <summary>搜索框输入（进程名或 PID）。</summary>
    public string SearchText
    {
        get => _searchText;
        set => SetField(ref _searchText, value);
    }

    /// <summary>搜索结果行（判定结果+依据文案投影）。</summary>
    public IReadOnlyList<SearchResultRow> SearchResults
    {
        get => _searchResults;
        private set => SetField(ref _searchResults, value);
    }

    /// <summary>搜索状态行（匹配数/无匹配/输入引导；空=未搜索）。</summary>
    public string SearchStatusText
    {
        get => _searchStatusText;
        private set => SetField(ref _searchStatusText, value);
    }

    public bool IsScanning => StateMachine.State == AppState.Scanning;

    public ICommand StartScanCommand { get; }

    public ICommand SearchCommand { get; }

    /// <summary>右键加白命令（参数=行；仅 ✅/⚠️ 级已展示态可用，PRD F4）。</summary>
    public ICommand WhitelistCommand { get; }

    /// <summary>
    /// 取消命令（T-10，PRD F3-5）：转调 IReleaser.Cancel（跳过未开始树+等待进行中收尾）。
    /// 仅释放中态且 releaser 已注入时可用（矩阵 CancelReleaseEnabled 双保险）。
    /// </summary>
    public ICommand CancelReleaseCommand { get; }

    /// <summary>一键释放命令（T-16：确认弹窗→Execute 编排；仅已展示态且 releaser 已注入时可用）。</summary>
    public ICommand ReleaseCommand { get; }

    /// <summary>白名单管理面板开关（T-26，F4 工具栏入口；不触判定链，任意态可用）。</summary>
    public ICommand ToggleWhitelistPanelCommand { get; }

    /// <summary>逐项移除命令（T-26，F4；参数=面板条目行）。</summary>
    public ICommand RemoveWhitelistEntryCommand { get; }

    /// <summary>打开日志文件入口（T-26，F6；日志尚未生成时打开所在数据目录）。</summary>
    public ICommand OpenLogFileCommand { get; }

    /// <summary>打开数据目录入口（T-26，F6；日志所在目录=用户数据目录锚点）。</summary>
    public ICommand OpenDataDirectoryCommand { get; }

    /// <summary>关闭报告命令（结果展示态回已展示，PRD §3.6 离开条件）。</summary>
    public ICommand CloseReportCommand { get; }

    /// <summary>以管理员身份重启命令（T-16 提权重启编排；内容条件见 <see cref="CanRestartElevated"/>）。</summary>
    public ICommand RestartElevatedCommand { get; }

    /// <summary>
    /// 提权重启入口可用性（PRD F3 触发，态级矩阵×内容条件叠加）：
    /// 已展示态=列表含预标 RequiresElevation 项；结果展示态=报告含 NeedsElevation 失败项。
    /// 判定与清单收集同源（<see cref="ElevationFailureItems"/>，防入口可见而清单为空的失同步）。
    /// </summary>
    public bool CanRestartElevated =>
        StateMachine.Availability.RestartElevatedEnabled && ElevationFailureItems().Count > 0;

    public event PropertyChangedEventHandler? PropertyChanged;

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

    /// <summary>
    /// 一键释放编排（T-16，PRD F3）：矩阵准入 → 收集勾选 → Plan（树构建+保护集标记）→
    /// 确认弹窗（N=非空树数，X=按节点 Pid 去重的将结束字节合计——祖先与后代同勾选时
    /// Execute 所有权预分配[T-09 裁决②]只执行一次，弹窗聚合须同口径防重复计字）；
    /// 取消停留已展示态不产生触发 → ReleaseConfirmed → Execute（挪离 UI 线程）+
    /// 树级进度（事件编组收口）→ 完成事件收口（<see cref="CompleteRelease"/>）。
    /// 规划失败收口为提示（停留已展示态可重试）；执行链违约（Core 结构不可达）经
    /// <see cref="AppTrigger.ReleaseFailed"/> 防御出口回已展示态，防状态滞留致关窗死锁。
    /// </summary>
    public async Task ReleaseAsync()
    {
        if (_releaser is null || _snapshot is null || !StateMachine.Availability.ReleaseEnabled)
        {
            return; // 准入（矩阵唯一权威）：非已展示态/releaser 未接线一律空安全返回
        }

        var selectedPids = Groups.SelectMany(g => g.Rows)
            .Where(r => r.IsChecked).Select(r => r.Pid).ToHashSet();
        if (selectedPids.Count == 0)
        {
            return; // 未勾选：无可释放（停留已展示，不弹空弹窗）
        }

        var snapshot = _snapshot;
        var request = new ReleaseRequest(Guid.NewGuid(), snapshot.TakenAtUtc, selectedPids, DateTime.UtcNow);
        IReadOnlyList<TreePlan> plans;
        try
        {
            var whitelist = _whitelistStore.Snapshot();
            var rulePack = _coordinator.LoadRulePackSafe();
            plans = await Task.Run(() => _releaser.Plan(request, snapshot, whitelist, rulePack))
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 规划失败（如保护名单不可用 fail-closed，T-08 裁决⑥）：停留已展示态，可重试或重新扫描
            ReleaseFailedMessage = $"释放规划失败：{ex.Message}";
            return;
        }

        // 空计划壳树（勾选根被保护集命中）不计入“N 树”（T-08 裁决②呈现侧过滤口径）；全空=无可释放
        var treeCount = plans.Count(p => p.Nodes.Count > 0);
        // X 按节点 Pid 去重（祖先与后代同勾选时跨树重复节点只计一次，与 Execute committedByPid 同口径）
        var totalBytes = plans.SelectMany(p => p.Nodes)
            .GroupBy(n => n.Snapshot.Pid)
            .Select(g => g.First().Snapshot.PrivateCommittedBytes)
            .Sum();
        if (treeCount == 0)
        {
            return;
        }

        // 被拉起预期上下文（报告提示消费）：勾选项的判定（WouldBeRevived/来源，判定单一事实源）；
        // 重复 PID（契约违约脏数据）按首条收口不抛——与 TreeIndex.Build 同口径
        _releaseContext = _classifications
            .Where(c => selectedPids.Contains(c.Pid))
            .GroupBy(c => c.Pid)
            .Select(g => g.First())
            .ToDictionary(c => c.Pid);

        if (_confirmDialog is null || !_confirmDialog.Confirm(treeCount, totalBytes))
        {
            return; // 弹窗取消：停留已展示态（弹窗取消不产生状态触发，PRD §3.6）
        }

        ReleaseFailedMessage = null;
        if (!StateMachine.TryTransition(AppTrigger.ReleaseConfirmed))
        {
            return; // 状态机拒绝（竞态重复触发）：不执行，亦不触碰在途释放的进度计数
        }

        _totalTrees = plans.Count; // 进度分母=全部计划树（含壳树——Core 对其立即发 Skipped 终态）
        _finishedTrees.Clear();
        _cancelRequested = false;
        _releaseTask = Task.Run(() => _releaser.Execute(request, plans));
        try
        {
            await _releaseTask.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 防御边界：Core 结构不可达（树内异常映射逐项+订阅者隔离）。如实提示不伪造报告；
            // 经 ReleaseFailed 防御出口回已展示态——无报告即无 ReleaseCompleted 出口，
            // 不放行将致状态永久滞留释放中（关窗互递归死锁，cross-review 收口）
            ReleaseFailedMessage = $"释放执行失败：{ex.GetType().Name}: {ex.Message}";
            StateMachine.TryTransition(AppTrigger.ReleaseFailed);
        }
    }

    /// <summary>
    /// 主窗口关闭准入（释放中关窗语义，PRD §3.6 注）：释放中=转调 Cancel（取消未开始树、
    /// 进行中树等待收尾）并返回 false（调用方 e.Cancel=true，改走 <see cref="SettleReleaseAsync"/>
    /// 等待收尾后再关）；非释放中（含扫描中——直接退出丢弃部分结果，PRD §3.6）返回 true 直接关。
    /// 执行链违约的滞留态由 <see cref="AppTrigger.ReleaseFailed"/> 出口放行（单一机制，不在此处
    /// 另设终局放行——防“收口未处理的窗口期被提前关窗”绕过等待语义）。
    /// </summary>
    public bool PrepareClose()
    {
        if (StateMachine.State != AppState.Releasing)
        {
            return true;
        }

        RequestCancel();
        return false;
    }

    /// <summary>取消意图（PRD F3-5）：置取消标记（报告/状态行据此区分“已取消”与“全部完成”，
    /// R03 GWT“如实反映已执行/已取消”）+ 转调 Core（跳过未开始树，进行中树等待收尾）。</summary>
    private void RequestCancel()
    {
        _cancelRequested = true;
        _releaser?.Cancel();
    }

    /// <summary>
    /// 等待在途释放收尾（关窗路径第二段）：Execute 全部树终态 + 日志追加落盘。
    /// 幂等（无在途=立即返回）；Execute 意外异常一并吞（异常已收口为提示，不阻断关窗）。
    /// </summary>
    public async Task SettleReleaseAsync()
    {
        var release = _releaseTask;
        if (release is not null)
        {
            try
            {
                await release.ConfigureAwait(true);
            }
            catch
            {
                // Execute 异常已在 ReleaseAsync 收口为提示；此处仅等待终局
            }
        }

        var log = _logTask;
        if (log is not null)
        {
            try
            {
                await log.ConfigureAwait(true);
            }
            catch
            {
                // Append 契约不抛（storage 法条）；防御壳兜编排异常
            }
        }
    }

    /// <summary>
    /// 全量判定查询（R02 搜索框）：Query 消费最近一次 Classify 全量输出（判定单一事实源，不重算）；
    /// 纯数字输入按 PID 查、其余按名查（与 Core Query 双参语义对齐）。空输入/未扫描给引导不查询；
    /// 序号守卫丢弃过期响应（连续查询后发起者胜，防旧结果覆盖新输入），异常收口到状态行。
    /// </summary>
    public async Task SearchAsync()
    {
        var seq = ++_searchSeq;
        try
        {
            var text = SearchText.Trim();
            if (text.Length == 0 || _snapshot is null)
            {
                SearchResults = [];
                SearchStatusText = "输入进程名或 PID 后查询";
                return;
            }

            var pid = int.TryParse(text, out var parsedPid) ? parsedPid : (int?)null;
            var name = pid.HasValue ? null : text;
            var snapshot = _snapshot;
            var classifications = _classifications;
            var results = await Task.Run(() => _rules.Query(snapshot, classifications, name, pid))
                .ConfigureAwait(true);
            if (seq != _searchSeq)
            {
                return; // 过期响应：期间有新查询发起，丢弃旧结果
            }

            SearchResults = results.Select(r => new SearchResultRow(
                r.Pid, r.Name, DisplayText.QueryOutcome(r.Outcome), DisplayText.Reason(r.Bases))).ToList();
            SearchStatusText = results.Count == 0 ? "无匹配进程" : $"匹配 {results.Count} 项";
        }
        catch (Exception ex)
        {
            if (seq != _searchSeq)
            {
                return;
            }

            SearchResults = [];
            SearchStatusText = $"查询失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 右键加白（PRD F4 + ③.s4 裁决⑥）：仅 ✅/⚠️ 级、已展示态（CanExecute+守卫双保险）。
    /// 成功链：写入白名单（挪离 UI 线程）→ 即时重跑 Classify → 列表重建（该行移除）+ 排除计数同步；
    /// 收口代际守卫：重判期间若发生新扫描（_snapshot 换代）则丢弃陈旧重判结果，防旧数据覆盖新扫描；
    /// 失败链分流：写白名单失败=“加白失败”；写成功但重判失败=“已加入白名单，刷新失败（下次扫描生效）”。
    /// </summary>
    public async Task WhitelistAsync(ClassificationRow? row)
    {
        if (row is null || !row.CanWhitelist || _snapshot is null
            || !StateMachine.Availability.ListInputEnabled)
        {
            return;
        }

        var snapshot = _snapshot;
        var target = snapshot.Snapshots.FirstOrDefault(s => s.Pid == row.Pid);
        if (target is null)
        {
            return; // 快照缺项（契约违约）的占位行不可加白：占位名写入存储将成为永不匹配的脏条目
        }

        try
        {
            await Task.Run(() => _whitelistStore.Add(row.ProcessName, target.ExecutablePath))
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            WhitelistNotice = $"加白失败：{ex.Message}";
            return;
        }

        try
        {
            var next = await Task.Run(() => _coordinator.ReclassifyAsync(snapshot)).ConfigureAwait(true);
            // 代际复验：重判期间发生新扫描（_snapshot 换代）则丢弃本次陈旧重判结果
            // （新扫描已取最新白名单快照重判，无需合并）；状态由新扫描链收口
            if (!ReferenceEquals(_snapshot, snapshot) || StateMachine.State != AppState.ResultsShown)
            {
                return;
            }

            ApplyResult(snapshot, next);
            WhitelistNotice = null;
        }
        catch (Exception ex)
        {
            // 写白名单已成功（先盘后内存），仅刷新失败：文案与事实一致，防误导重试
            WhitelistNotice = $"已加入白名单，但刷新列表失败：{ex.Message}（下次扫描生效）";
        }
    }

    /// <summary>
    /// 白名单管理面板开关（T-26，F4）：每次打开重取存储全量；常开期间不追踪加白等外部变更
    /// （下次打开/移除刷新生效——非模态面板与主列表并存，实时追踪属 T-16 后的交互增强项）。
    /// 面板不触判定链与状态机（任意五态可用——移除仅写存储，恢复判定走重扫，PRD F4）。
    /// </summary>
    public void ToggleWhitelistPanel()
    {
        IsWhitelistPanelOpen = !IsWhitelistPanelOpen;
        if (IsWhitelistPanelOpen)
        {
            TryRefreshWhitelistPanel();
        }
    }

    /// <summary>
    /// 逐项移除（T-26，F4）：按名写存储（挪离 UI 线程，存储侧 OrdinalIgnoreCase 语义）→
    /// 刷新面板（行消失即反馈）并清旧失败提示。移除后不即时重判——恢复参与判定走重扫
    /// （PRD F4"移除后重新扫描即恢复参与判定"，与加白的即时性裁决⑥刻意分立）。
    /// 失败链：盘写异常提示原因，条目保留；目标已不存在（面板常开期间被移除）照常刷新，不误报成败；
    /// 刷新段兜底（写盘已成功，失败仅提示不静默——对齐 WhitelistAsync 两段式先例，命令为 fire-and-forget）。
    /// </summary>
    public async Task RemoveWhitelistEntryAsync(WhitelistEntryRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => _whitelistStore.Remove(row.Name)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            WhitelistNotice = $"移除白名单失败：{ex.Message}";
            return;
        }

        // 刷新成功才清旧失败提示；刷新自身失败时保留其失败提示（防兜底提示被无差别清除）
        if (TryRefreshWhitelistPanel())
        {
            WhitelistNotice = null;
        }
    }

    /// <summary>面板刷新兜底壳：绑定订阅方异常不因 fire-and-forget 静默（对齐 StartScanAsync 全路径兜底模式）。
    /// 返回刷新是否成功，供调用方决定是否清除先前提示。</summary>
    private bool TryRefreshWhitelistPanel()
    {
        try
        {
            // List() 为锁内内存拷贝（KB 级常态亚毫秒）；与在途盘写互斥的持锁窗口由存储口径承担，不挪线程
            WhitelistEntries = _whitelistStore.List().Select(ToEntryRow).ToList();
            return true;
        }
        catch (Exception ex)
        {
            WhitelistNotice = $"白名单面板刷新失败：{ex.Message}";
            return false;
        }
    }

    private static WhitelistEntryRow ToEntryRow(WhitelistEntry entry) => new(
        entry.Name,
        DisplayText.WhitelistAddedAt(entry.AddedAtUtc),
        string.IsNullOrWhiteSpace(entry.Path) ? DisplayText.EmptyValue : entry.Path,
        string.IsNullOrWhiteSpace(entry.Note) ? DisplayText.EmptyValue : entry.Note);

    /// <summary>打开日志文件（T-26，F6）：文件在位直开；尚未生成（从未释放过）打开所在数据目录。</summary>
    private void OpenLogFile()
    {
        if (_logStore is null)
        {
            return; // CanExecute 已挡，双保险
        }

        var path = _logStore.LogFilePath;
        if (File.Exists(path))
        {
            OpenInShell(path);
        }
        else
        {
            OpenDataDirectory();
        }
    }

    /// <summary>打开数据目录（T-26，F6）：日志所在目录=用户数据目录锚点（storage 法-4 同目录）。</summary>
    private void OpenDataDirectory()
    {
        if (_logStore is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_logStore.LogFilePath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            // 目录未建（从未写过盘）先建，防 shell 打开落空；建失败（盘不可用/权限拒绝）与打开失败同收口
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            WhitelistNotice = $"打开失败：{ex.Message}";
            return;
        }

        OpenInShell(directory);
    }

    private void OpenInShell(string path)
    {
        try
        {
            _shellOpen(path);
        }
        catch (Exception ex)
        {
            // shell 打开失败（无默认程序/权限拒绝等环境态）：轻提示收口，不炸命令链
            WhitelistNotice = $"打开失败：{ex.Message}";
        }
    }

    private static void DefaultShellOpen(string path)
    {
        // using 释放 Process 句柄（打开动作不持有被启动程序的生命周期，防句柄依赖终结器）
        using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>
    /// 释放结果收口（ReleaseCompleted 事件编组后的唯一收口路径，PRD §3.6“全部树完成/取消”同入口）：
    /// 存报告 → 已结束项移除（PRD F3-6，幸存行保留用户勾选/展开态）→ 状态机释放完成转换
    /// （释放中→结果展示）→ 日志追加编排单路径（IReleaseLogStore.Append，ReleaseId 幂等去重——
    /// 重复投递不重复落行，storage 契约“一次释放至多一次 Append”）。
    /// 状态机拒绝（非释放中态调用）=转换无操作，报告仍留存。
    /// </summary>
    public void CompleteRelease(ReleaseReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        LastReleaseReport = report;
        RemoveFinishedItems(report);
        StateMachine.TryTransition(AppTrigger.ReleaseCompleted);
        AppendReleaseLog(report);
    }

    /// <summary>以管理员身份重启（T-16，PRD F3-6）：携带失败项清单的重启参数拉起新实例（runAs），
    /// 成功后本实例退出（互斥让位由新实例有限等待保证，裁决⑨）；UAC 拒绝停留普通权限（PRD §3.7）。</summary>
    private void RestartElevated()
    {
        if (!CanRestartElevated || _restarter is null)
        {
            return;
        }

        var failedItems = ElevationFailureItems();
        try
        {
            _restarter.Restart(failedItems);
            _shutdown?.Invoke();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            WhitelistNotice = "已取消管理员授权，停留在普通权限"; // UAC 拒绝（ERROR_CANCELLED）
        }
        catch (Exception ex)
        {
            WhitelistNotice = $"管理员重启失败：{ex.Message}";
        }
    }

    /// <summary>失败项清单（重启参数载荷，PRD F3-6 识别口径=名称+可执行路径）：
    /// 报告态取 NeedsElevation 逐项结果；已展示态取预标 RequiresElevation 项（名称/路径取自快照索引）。
    /// 入口可用性判定与清单收集同源单点。</summary>
    private IReadOnlyList<RestartFailedItem> ElevationFailureItems()
    {
        if (StateMachine.State == AppState.ReportShown && LastReleaseReport is not null)
        {
            return LastReleaseReport.Items
                .Where(i => i.Outcome == ReleaseItemOutcome.NeedsElevation)
                .Select(i => new RestartFailedItem(i.Name, i.ExecutablePath))
                .ToList();
        }

        if (StateMachine.State != AppState.ResultsShown || _snapshot is null)
        {
            return [];
        }

        var index = TreeIndex.Build(_snapshot);
        return _classifications
            .Where(c => c.RequiresElevation)
            .Select(c =>
            {
                index.TryGet(c.Pid, out var snapshot);
                return new RestartFailedItem(
                    DisplayText.PidFallbackName(snapshot?.Name, c.Pid), snapshot?.ExecutablePath);
            })
            .ToList();
    }

    /// <summary>已结束项移除（PRD F3-6）：Released/ForceKilled/Exited 项对应行从列表移除并重建分组
    /// （未结束项——需管理员/被拦截等——保留供提权重启后再处理）；快照本体保留（行详情仍可渲染）；
    /// 幸存行保留用户勾选/展开态（行实例随重建换新，逐 Pid 迁移旧态，防用户手动取消勾选被重置回默认）。</summary>
    private void RemoveFinishedItems(ReleaseReport report)
    {
        if (_snapshot is null)
        {
            return; // 无列表上下文（防御）：报告仍照常呈现
        }

        var finishedPids = report.Items
            .Where(i => i.Outcome is ReleaseItemOutcome.Released
                or ReleaseItemOutcome.ForceKilled or ReleaseItemOutcome.Exited)
            .Select(i => i.Pid)
            .ToHashSet();
        if (finishedPids.Count == 0)
        {
            return;
        }

        var survivorStates = Groups.SelectMany(g => g.Rows)
            .Where(r => !finishedPids.Contains(r.Pid))
            .GroupBy(r => r.Pid)
            .Select(g => g.First()) // 重复 PID（契约违约脏数据）按首条收口，与 TreeIndex.Build 同口径
            .ToDictionary(r => r.Pid, r => (r.IsChecked, r.IsExpanded));
        _classifications = _classifications.Where(c => !finishedPids.Contains(c.Pid)).ToList();
        RebuildGroups(_classifications);
        foreach (var row in Groups.SelectMany(g => g.Rows))
        {
            if (survivorStates.TryGetValue(row.Pid, out var state))
            {
                row.IsChecked = state.IsChecked;
                row.IsExpanded = state.IsExpanded;
            }
        }
    }

    /// <summary>日志追加编排单路径（AC：ReleaseCompleted 后调 IReleaseLogStore.Append）：
    /// 挪离 UI 线程；追加与结果回填在同一任务内串行（<see cref="SettleReleaseAsync"/> 等待本任务
    /// 即保证回填完成，无并行延续竞争）；写失败不阻塞收口（PRD §3.7“未留痕”）。</summary>
    private void AppendReleaseLog(ReleaseReport report)
    {
        if (_logStore is null || !_loggedReleaseIds.Add(report.ReleaseId))
        {
            return; // 未接线（LogPersisted 恒 null=未尝试）或同报告重复投递（Append 非幂等契约）
        }

        _logTask = AppendAndBackfillAsync(report);
    }

    private async Task<ReleaseLogAppendResult> AppendAndBackfillAsync(ReleaseReport report)
    {
        ReleaseLogAppendResult result;
        try
        {
            result = await Task.Run(() => _logStore!.Append(report)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            result = new ReleaseLogAppendResult(Persisted: false, Error: ex.Message); // Append 契约不抛，防御壳
        }

        if (ReferenceEquals(LastReleaseReport, report))
        {
            // 期间无新释放覆盖：回填写结果（record 换实例）并刷新报告文案；
            // 错误原因同受代际守卫约束（防旧释放的失败原因串扰到新报告，cross-review 收口）
            _logError = result.Persisted ? null : result.Error;
            LastReleaseReport = report with { LogPersisted = result.Persisted };
            OnPropertyChanged(nameof(ReleaseReportText));
        }

        return result;
    }

    /// <summary>树级进度收口：终态（Done/Skipped/Failed）计数，非终态不计数（进行中呈现由状态文本承载）；
    /// 计数变化重算状态文本（setter 变更时自行通知，进度折叠进状态文本，树级粒度，ui.md §6 情报条）。</summary>
    private void OnTreeProgress(int rootPid, TreeState state)
    {
        if (state is TreeState.Done or TreeState.Skipped or TreeState.Failed
            && _finishedTrees.Add(rootPid))
        {
            UpdateStatusText();
        }
    }

    /// <summary>
    /// 开始扫描核心链：状态机准入 → 四步链挪离调用线程 → 成功收口/失败保留旧结果。
    /// </summary>
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
            ApplyResult(run.Outcome.Snapshot, run.Outcome.Classifications);
            LastScanTakenAtUtc = run.Outcome.Snapshot.TakenAtUtc;
            ScanFailedMessage = null;
            StateMachine.TryTransition(AppTrigger.ScanCompleted);
        }
        else
        {
            // 保留旧结果：仅置失败提示与状态，不更新列表/时间戳
            ScanFailedMessage = $"扫描失败：{run.FailureReason}";
            StateMachine.TryTransition(AppTrigger.ScanFailed);
        }
    }

    /// <summary>
    /// 扫描成功/加白重判共用的结果收口：快照+全量分类 → 列表投影（分组/排除计数）；
    /// 数据换代即失效搜索面板（旧搜索结论与新列表共存会自相矛盾，面板随空状态折叠）。
    /// 加白重判不触碰 <see cref="LastScanTakenAtUtc"/>（同一快照，无新扫描时点）。
    /// </summary>
    private void ApplyResult(ScanResult snapshot, IReadOnlyList<Classification> classifications)
    {
        _snapshot = snapshot;
        Classifications = classifications;
        RebuildGroups(classifications);
        SearchResults = [];
        SearchStatusText = string.Empty;
        _searchSeq++; // 在途查询的响应一并作废
    }

    /// <summary>三级分组投影：仅渲染 ✅/⚠️/🚫（Whitelisted 进排除计数、Unmatched 不可见，PRD F1/F2/F4）；
    /// 零推荐时组整体不渲染（空态文案承载，PRD §3.7“扫描零推荐”行）；
    /// 快照索引整组构建一次（O(n)），行投影共享，防 600 进程全量重建时每行重复扫描；
    /// 重建后按重启链失败项清单叠加高亮（PRD F3-6，不自动恢复勾选）。</summary>
    private void RebuildGroups(IReadOnlyList<Classification> classifications)
    {
        var index = TreeIndex.Build(_snapshot!);
        Groups = new[] { Level.Recommend, Level.Caution, Level.Protected }
            .Select(level => new LevelGroup(level, ProjectRows(classifications, level, index)))
            .Where(g => g.Rows.Count > 0)
            .ToList();
        // 折叠区排除项清单（T-26，F2）：单次物化 Whitelisted 集，计数与投影同源为结构事实；
        // 快照缺项（契约违约脏数据）以"未知进程"占位不炸投影（PID 由行模板统一承载，防重复拼接）
        var whitelisted = classifications.Where(c => c.Level == Level.Whitelisted).ToList();
        WhitelistedExcludedCount = whitelisted.Count;
        OnPropertyChanged(nameof(WhitelistedExcludedCount));
        WhitelistedExcludedRows = whitelisted
            .Select(c => index.TryGet(c.Pid, out var process)
                ? new WhitelistedExcludedRow(c.Pid, process.Name)
                : new WhitelistedExcludedRow(c.Pid, "未知进程"))
            .ToList();
        if (_restartFailedItems is { Count: > 0 })
        {
            foreach (var row in Groups.SelectMany(g => g.Rows))
            {
                row.IsHighlighted = MatchesRestartFailure(row, index);
            }
        }
    }

    /// <summary>重启链失败项匹配（PRD F3-6 识别口径=名称+可执行路径）：
    /// 名称 OrdinalIgnoreCase；路径两侧均非空时一并比对（OrdinalIgnoreCase），清单路径缺失=不可读，
    /// 仅按名称匹配（T-01 裁决：路径不可读为采集常态，名称已具识别力）。</summary>
    private bool MatchesRestartFailure(ClassificationRow row, TreeIndex index)
    {
        foreach (var item in _restartFailedItems!)
        {
            if (!string.Equals(item.Name, row.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.ExecutablePath))
            {
                return true;
            }

            if (index.TryGet(row.Pid, out var snapshot)
                && string.Equals(snapshot.ExecutablePath, item.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<ClassificationRow> ProjectRows(
        IReadOnlyList<Classification> classifications, Level level, TreeIndex index) =>
        classifications.Where(c => c.Level == level).Select(c => new ClassificationRow(c, index));

    /// <summary>状态提示文案（五态骨架映射+释放进度计数+报告可重扫提示；零推荐空态=PRD §3.7 行）。</summary>
    private void UpdateStatusText()
    {
        StatusText = StateMachine.State switch
        {
            AppState.NotScanned => "点击“开始扫描”检查可安全结束的残留进程",
            AppState.Scanning => "正在扫描…",
            AppState.ResultsShown when Classifications.Count == 0 => "当前无可释放的进程",
            AppState.ResultsShown when LastScanTakenAtUtc.HasValue =>
                $"快照时间：{LastScanTakenAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            AppState.Releasing when _totalTrees > 0 =>
                $"正在释放…（已完成 {_finishedTrees.Count}/{_totalTrees} 棵树）",
            AppState.Releasing => "正在释放…",
            AppState.ReportShown when _cancelRequested =>
                $"已取消，{DisplayText.RescanHint}",
            AppState.ReportShown => $"释放完成，{DisplayText.RescanHint}",
            _ => string.Empty,
        };
    }

    private void OnStateChanged()
    {
        UpdateStatusText();
        RaiseAll();
    }

    /// <summary>整组绑定属性通知（矩阵/状态均为低频整体变化，统一刷新）；
    /// 集合属性（Groups/SearchResults/计数）不在列：各自由赋值点发通知，此处重发会触发 ItemsControl 全量重建
    /// （Classifications 同为集合属性但无 XAML 绑定[列表绑 Groups]，仅供测试消费，列入无害）。</summary>
    private void RaiseAll()
    {
        // StartScanCommand 的属性通知会触发 WPF 重新取绑定命令值并重查 CanExecute——
        // 仅靠 CommandManager.RequerySuggested 需等下一次输入/焦点事件，按钮置灰/恢复会滞后一拍
        OnPropertyChanged(nameof(StartScanCommand));
        OnPropertyChanged(nameof(SearchCommand));
        OnPropertyChanged(nameof(WhitelistCommand));
        OnPropertyChanged(nameof(CancelReleaseCommand));
        OnPropertyChanged(nameof(ReleaseCommand));
        OnPropertyChanged(nameof(CloseReportCommand));
        OnPropertyChanged(nameof(RestartElevatedCommand));
        OnPropertyChanged(nameof(ToggleWhitelistPanelCommand));
        OnPropertyChanged(nameof(RemoveWhitelistEntryCommand));
        OnPropertyChanged(nameof(OpenLogFileCommand));
        OnPropertyChanged(nameof(OpenDataDirectoryCommand));
        OnPropertyChanged(nameof(Availability));
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(Classifications));
        OnPropertyChanged(nameof(LastScanTakenAtUtc));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ScanFailedMessage));
        OnPropertyChanged(nameof(ReleaseFailedMessage));
        OnPropertyChanged(nameof(WhitelistNotice));
        OnPropertyChanged(nameof(LastReleaseReport));
        OnPropertyChanged(nameof(ReleaseReportText));
        OnPropertyChanged(nameof(CanRestartElevated));
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

/// <summary>搜索结果行（QueryResult 的文案投影：判定结果+依据，R02 搜索框输出）。</summary>
public sealed record SearchResultRow(int Pid, string Name, string OutcomeText, string ReasonText);

/// <summary>
/// 白名单面板条目行（T-26，WhitelistEntry 只读投影）：名称/添加时间本地文案/路径/备注；
/// 可空元数据（路径/备注）以"—"显式占位（PRD F2 空值口径同源）。
/// </summary>
public sealed record WhitelistEntryRow(string Name, string AddedAtText, string PathText, string NoteText);

/// <summary>折叠区排除项行（T-26，F2"因白名单排除 N 项"展开清单：PID+进程名）。</summary>
public sealed record WhitelistedExcludedRow(int Pid, string Name);
