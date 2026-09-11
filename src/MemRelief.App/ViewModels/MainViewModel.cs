using System.ComponentModel;
using System.Windows.Input;
using MemRelief.App.Scanning;
using MemRelief.App.State;
using MemRelief.App.Text;
using MemRelief.Core.Contracts;
using MemRelief.Core.Releaser;
using MemRelief.Core.Rules;
using MemRelief.Core.Scanner;
using MemRelief.Core.Storage;

namespace MemRelief.App.ViewModels;

/// <summary>
/// 主窗口 ViewModel（ui 模块编排面）：持有五态状态机宿主，收口扫描链编排结果到绑定面。
/// 线程模型：StartScanAsync 由 UI 线程发起，await 延续回捕获的 UI 上下文后更新绑定属性
/// （await 上下文恢复即编组，ui.md §6 法条；本包不订阅 Core 后台线程事件，
/// releaser 事件接线归 T-16，届时经 Dispatcher 编组后调 <see cref="CompleteRelease"/>）。
/// 绑定面刷新策略：整组属性统一 RaiseAll（列表快照整体替换，无逐项高频更新）。
/// 三级列表（T-15）：分组投影 <see cref="Groups"/>、搜索框全量查询（IRulesEngine.Query）、
/// 右键加白即时重判（③.s4 裁决⑥）、白名单排除计数。
/// 释放编排（T-10 本包切片）：取消命令（<see cref="CancelReleaseCommand"/>）与
/// 结果报告收口（<see cref="CompleteRelease"/>）；Execute 触发/进度呈现/日志追加归 T-16。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ScanCoordinator _coordinator;
    private readonly IScanner _overviewSampler;
    private readonly IRulesEngine _rules;
    private readonly IWhitelistStore _whitelistStore;
    private readonly IReleaser? _releaser;

    private IReadOnlyList<Classification> _classifications = [];
    private ScanResult? _snapshot;
    private IReadOnlyList<LevelGroup> _groups = [];
    private DateTime? _lastScanTakenAtUtc;
    private MemoryOverview? _overview;
    private string _overviewSummary = "—";
    private string? _scanFailedMessage;
    private string _statusText = string.Empty;
    private string _searchText = string.Empty;
    private IReadOnlyList<SearchResultRow> _searchResults = [];
    private string _searchStatusText = string.Empty;
    private int _searchSeq;
    private string? _whitelistNotice;
    private ReleaseReport? _lastReleaseReport;

    /// <summary>
    /// releaser 可空注入（T-10 取消编排）：生产组合根随 T-16 释放接线时传入；
    /// 未注入时取消命令不可用（Releasing 态无接线不可达，双保险，无静默降级路径）。
    /// </summary>
    public MainViewModel(
        UiStateMachine stateMachine,
        ScanCoordinator coordinator,
        IScanner overviewSampler,
        IRulesEngine rules,
        IWhitelistStore whitelistStore,
        IReleaser? releaser = null)
    {
        StateMachine = stateMachine;
        _coordinator = coordinator;
        _overviewSampler = overviewSampler;
        _rules = rules;
        _whitelistStore = whitelistStore;
        _releaser = releaser;
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
            () => _releaser?.Cancel(),
            () => _releaser is not null && StateMachine.Availability.CancelReleaseEnabled);
        // 启动装载即感知白名单损坏自愈（IWhitelistStore.Recovery 唯一通道，storage.md §4.1）；
        // 提示携带 Recovery.Reason：备份失败变体（原文件原地保留）与已重置变体的事实不同，禁固定文案掩盖差异
        if (whitelistStore.Recovery is not null)
        {
            WhitelistNotice = $"白名单异常已处理：{whitelistStore.Recovery.Reason}";
        }

        OnStateChanged();
    }

    /// <summary>
    /// 五态状态机宿主（测试铺态与 T-16 释放编排消费；View 只读绑定 State/Availability，
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

    /// <summary>概览摘要文本；采样失败显示“—”（PRD §3.7“内存信息读取失败”行）。</summary>
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

    /// <summary>白名单轻提示（损坏自愈重置/加白失败；null=无提示）。</summary>
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

    /// <summary>白名单提示可见性（XAML 布尔转换绑定用）。</summary>
    public bool HasWhitelistNotice => _whitelistNotice != null;

    /// <summary>最近一次释放结果报告（T-10 结果报告收口；呈现面板归 T-16 绑定此值）。</summary>
    public ReleaseReport? LastReleaseReport
    {
        get => _lastReleaseReport;
        private set => SetField(ref _lastReleaseReport, value);
    }

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
    /// 释放结果收口（T-10 结果报告编排面）：存报告 + 状态机释放完成转换（释放中 → 结果展示，
    /// PRD §3.6 出口条件"全部树完成/取消"同入口收口）。调用方=T-16 的 ReleaseCompleted 事件接线
    /// （工作线程事件须经 Dispatcher 编组后抵达，ui.md §6 法条；本方法自身不做编组）。
    /// 状态机拒绝（非释放中态调用）= 转换无操作，报告仍留存供查看；
    /// 报告呈现（双释放量/跳过说明/日志结果）归 T-16，绑定 <see cref="LastReleaseReport"/>。
    /// 释放后概览刷新（F5 第三时点）由 T-17 在本收口点接线。
    /// </summary>
    public void CompleteRelease(ReleaseReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        LastReleaseReport = report;
        StateMachine.TryTransition(AppTrigger.ReleaseCompleted);
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
            ApplyResult(run.Outcome.Snapshot, run.Outcome.Classifications);
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
    /// 快照索引整组构建一次（O(n)），行投影共享，防 600 进程全量重建时每行重复扫描。</summary>
    private void RebuildGroups(IReadOnlyList<Classification> classifications)
    {
        var index = TreeIndex.Build(_snapshot!);
        Groups = new[] { Level.Recommend, Level.Caution, Level.Protected }
            .Select(level => new LevelGroup(level, ProjectRows(classifications, level, index)))
            .Where(g => g.Rows.Count > 0)
            .ToList();
        WhitelistedExcludedCount = classifications.Count(c => c.Level == Level.Whitelisted);
        OnPropertyChanged(nameof(WhitelistedExcludedCount));
    }

    private static IEnumerable<ClassificationRow> ProjectRows(
        IReadOnlyList<Classification> classifications, Level level, TreeIndex index) =>
        classifications.Where(c => c.Level == level).Select(c => new ClassificationRow(c, index));

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

    /// <summary>整组绑定属性通知（矩阵/状态均为低频整体变化，统一刷新）；
    /// 集合属性（Groups/SearchResults/计数）不在列：各自由赋值点发通知，此处重发会触发 ItemsControl 全量重建。</summary>
    private void RaiseAll()
    {
        // StartScanCommand 的属性通知会触发 WPF 重新取绑定命令值并重查 CanExecute——
        // 仅靠 CommandManager.RequerySuggested 需等下一次输入/焦点事件，按钮置灰/恢复会滞后一拍
        OnPropertyChanged(nameof(StartScanCommand));
        OnPropertyChanged(nameof(SearchCommand));
        OnPropertyChanged(nameof(WhitelistCommand));
        OnPropertyChanged(nameof(CancelReleaseCommand));
        OnPropertyChanged(nameof(Availability));
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(Classifications));
        OnPropertyChanged(nameof(LastScanTakenAtUtc));
        OnPropertyChanged(nameof(Overview));
        OnPropertyChanged(nameof(OverviewSummary));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ScanFailedMessage));
        OnPropertyChanged(nameof(WhitelistNotice));
        OnPropertyChanged(nameof(LastReleaseReport));
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
