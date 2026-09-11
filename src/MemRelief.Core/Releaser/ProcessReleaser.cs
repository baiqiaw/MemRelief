using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;

namespace MemRelief.Core.Releaser;

/// <summary>
/// 两段式执行主链（T-09）+ 取消与结果报告（T-10）：多树并行，每树 身份校验 → 优雅（WM_CLOSE，
/// 无窗口项跳过）→ 3s → TerminateProcess（releaser.md §4.1/§4.2，PRD F3-4）。
/// 树构建唯一承载 = <see cref="TreePlanner"/>（Plan 委托，Execute 只消费 TreePlan 不重建树——T-08 契约）。
/// 执行期去重（data-contracts §2 T-09 裁决②）：Execute 入口按传入计划序做全局 pid 所有权预分配，
/// 同一进程多树共现时仅属主树执行并产出唯一逐项结果（防并行双杀与释放量双计）；
/// 节点出现优先于跳过项出现（跨"祖先树连带跳过/自身树节点"场景，显式勾选的节点照常执行）。
/// 权限二分（T-10，PRD F3-7）：拒绝访问经 <see cref="AccessDeniedClassifier"/> 按所有者/令牌二分
/// ——打开受拒按快照所有者（无句柄令牌不可得）；强杀拒绝按执行期令牌名优先、快照 OwnerUser 回退；
/// 非拒绝错误维持机械 Blocked+错误码。
/// 取消（T-10，PRD F3-5）：<see cref="Cancel"/> 跳过未开始的树、进行中树等待收尾（不中断强杀）；
/// 取消收尾仍发完成事件（至多一次），报告记已执行部分（PRD F3-6）。
/// 报告采样（T-10，③.s4 裁决⑤）：Before=Execute 进入时（第一树启动前）、After=全部树终态后；
/// 主释放量=被结束进程（Released/ForceKilled）快照私有提交合计；校验释放量=commit 前后差
/// （可负如实输出，任一时点采样缺失为 null）。
/// </summary>
public sealed class ProcessReleaser : IReleaser
{
    private readonly TreePlanner _planner;
    private readonly ILiveProcessOpener _opener;
    private readonly IScanner? _overviewSampler;

    /// <summary>取消意图（PRD F3-5）：仅影响未开始树的启动，进行中树不受影响。Execute 入口重置。</summary>
    private volatile bool _cancelRequested;

    /// <summary>优雅等待总预算（PRD F3-4"等待 3 秒"；供合成单测压缩时钟，生产恒默认 3000）。</summary>
    internal int GraceWaitMs { get; set; } = 3000;

    /// <summary>
    /// 当前用户名（权限二分比对基准，T-01 裁决①与 OwnerUser 同为裸名、OrdinalIgnoreCase）。
    /// internal 可注入供合成单测固定，生产默认 <see cref="Environment.UserName"/>。
    /// </summary>
    internal string CurrentUserName { get; set; } = Environment.UserName;

    /// <summary>等待阶段轮询间隔（WaitExit(0) 探活 + 有界休眠，检测延迟 ≤ 本值）。</summary>
    private const int PollIntervalMs = 100;

    public event Action<int, TreeState>? TreeProgress;
    public event Action<ReleaseReport>? ReleaseCompleted;

    public ProcessReleaser()
        : this(new TreePlanner(), new Win32ProcessChannel(), new Scanner.Scanner())
    {
    }

    /// <summary>通道注入（合成单测以假 opener/采样器驱动全部分支；生产经默认构造走 Win32 通道）。</summary>
    internal ProcessReleaser(TreePlanner planner, ILiveProcessOpener opener, IScanner? overviewSampler = null)
    {
        _planner = planner;
        _opener = opener;
        _overviewSampler = overviewSampler;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TreePlan>> Plan(
        ReleaseRequest request,
        ScanResult scan,
        WhitelistSnapshot whitelist,
        RulePack rulePack) =>
        _planner.Plan(request, scan, whitelist, rulePack);

    /// <inheritdoc />
    public void Cancel() => _cancelRequested = true;

    /// <inheritdoc />
    public async Task<ReleaseReport> Execute(ReleaseRequest request, IReadOnlyList<TreePlan> plans)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plans);

        // 新一次释放生命周期：入口前残留的取消意图不作数（Cancel 幂等无副作用口径）；
        // 单释放并发由 ui 状态机准入保证（PRD §3.6 释放中禁用入口），Core 不另设重入锁
        _cancelRequested = false;

        var startedAtUtc = DateTime.UtcNow;
        var before = await SampleOverviewSafeAsync().ConfigureAwait(false); // 裁决⑤：进入时、第一树启动前

        // —— 违约输入快速失败（TreePlanner 同款口径，cross-review 收口）：勾选根互异、树内 pid 唯一
        //    由 Plan 产出保证；公开 API 对手拼计划防御（重复会双开句柄双杀并产出重复项） ——
        var rootPids = new HashSet<int>();
        foreach (var plan in plans)
        {
            ArgumentNullException.ThrowIfNull(plan);
            if (!rootPids.Add(plan.RootPid))
            {
                throw new ArgumentException($"计划列表含重复根 Pid {plan.RootPid}", nameof(plans));
            }

            var seenPids = new HashSet<int>();
            foreach (var node in plan.Nodes)
            {
                if (!seenPids.Add(node.Snapshot.Pid))
                {
                    throw new ArgumentException(
                        $"计划（根 Pid {plan.RootPid}）内含重复节点 Pid {node.Snapshot.Pid}", nameof(plans));
                }
            }
        }

        // —— 所有权预分配（裁决②）：节点优先于跳过项，传入计划序内先到先得（确定性，无运行期竞态） ——
        var nodeOwner = new Dictionary<int, TreePlan>();
        var committedByPid = new Dictionary<int, long>(); // 主释放量归因源：快照私有提交（PRD F3-6）
        foreach (var plan in plans)
        {
            foreach (var node in plan.Nodes)
            {
                nodeOwner.TryAdd(node.Snapshot.Pid, plan);
                committedByPid.TryAdd(node.Snapshot.Pid, node.Snapshot.PrivateCommittedBytes);
            }
        }

        var seenSkipPids = new HashSet<int>();
        var skipItems = new List<ReleaseItemResult>();
        foreach (var plan in plans)
        {
            foreach (var skipped in plan.SkippedNodes)
            {
                // 已被任一树作为节点执行的 pid 不再产出跳过项（单 pid 恰一项；执行胜出于连带跳过）
                if (!nodeOwner.ContainsKey(skipped.Snapshot.Pid) && seenSkipPids.Add(skipped.Snapshot.Pid))
                {
                    skipItems.Add(SkippedItem(skipped));
                }
            }
        }

        // —— 多树并行执行 ——
        var perTreeItems = await Task.WhenAll(
            plans.Select(plan => Task.Run(() => RunTree(plan, nodeOwner))));

        var after = await SampleOverviewSafeAsync().ConfigureAwait(false); // 裁决⑤：全部树终态后（含取消收尾）

        var items = skipItems.Concat(perTreeItems.SelectMany(x => x))
            .OrderBy(item => item.Pid)
            .ToList();

        var report = BuildReport(request, startedAtUtc, items, before, after, committedByPid);

        // 至多一次；随完成上下文发出（提供方不做线程切换，data-contracts §1.5）。
        // 订阅者异常就地隔离：释放已终局，通知通道 misuse 不得使 Execute 假败（cross-review 收口）
        try
        {
            ReleaseCompleted?.Invoke(report);
        }
        catch
        {
            // Core 无日志设施；报告仍正常返回
        }

        return report;
    }

    /// <summary>
    /// 报告组装（T-10）：逐项结果 + 双释放量（PRD F3-6）——主释放量只计被结束项
    /// （Released/ForceKilled，可归因）；校验释放量 = 系统 commit 前后差
    /// （正=下降；可负/失真如实输出，任一时点采样缺失为 null）。
    /// </summary>
    private static ReleaseReport BuildReport(
        ReleaseRequest request,
        DateTime startedAtUtc,
        IReadOnlyList<ReleaseItemResult> items,
        MemoryOverview? before,
        MemoryOverview? after,
        Dictionary<int, long> committedByPid)
    {
        var mainReleasedBytes = items
            .Where(item => item.Outcome is ReleaseItemOutcome.Released or ReleaseItemOutcome.ForceKilled)
            .Sum(item => committedByPid.TryGetValue(item.Pid, out var bytes) ? bytes : 0);
        long? checkReleasedBytes = before is not null && after is not null
            ? before.CommitBytes - after.CommitBytes
            : null;

        return new ReleaseReport(
            request.ReleaseId,
            request.RequestedAtUtc,
            startedAtUtc,
            DateTime.UtcNow,
            items)
        {
            Before = before,
            After = after,
            MainReleasedBytes = mainReleasedBytes,
            CheckReleasedBytes = checkReleasedBytes,
        };
    }

    /// <summary>
    /// 概览采样（经 scanner.SampleOverview，releaser.md §5）：挪离调用线程；
    /// 失败如实空缺（契约 Before/After 可空），不阻塞释放主链。
    /// </summary>
    private async Task<MemoryOverview?> SampleOverviewSafeAsync()
    {
        if (_overviewSampler is null)
        {
            return null;
        }

        try
        {
            return await Task.Run(_overviewSampler.SampleOverview).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    // —— 单树状态机：Pending → Closing → Waiting(3s) → Killing → Done；Pending → Killing 直达（整树无窗口）；无实际动作 → Skipped ——

    private List<ReleaseItemResult> RunTree(TreePlan plan, Dictionary<int, TreePlan> nodeOwner)
    {
        var items = new List<ReleaseItemResult>();
        // 意料外异常兜底域：登记在途句柄与所属快照，终态化时移除；异常时统一回收并逐 pid 补齐结果
        //（维持 §1.3"每 Pid 恰一项"，cross-review 收口；Failed 为防御性终态）
        var inflight = new Dictionary<int, (ProcessSnapshot Snapshot, ILiveProcess Live)>();
        Emit(plan.RootPid, TreeState.Pending);

        // 取消语义（PRD F3-5）：未开始的树跳过（Pending → Skipped），不产出逐项结果——
        // 报告记已执行部分（PRD F3-6）；已越过分派点的树（在途）不受后续取消影响
        if (_cancelRequested)
        {
            Emit(plan.RootPid, TreeState.Skipped);
            return items;
        }

        try
        {
            // 本树实际执行节点 = 树内节点 ∩ 属主为本树（去重壳树此处为空 → Skipped，其进程由属主树执行）
            var mine = plan.Nodes
                .Where(node => ReferenceEquals(nodeOwner[node.Snapshot.Pid], plan))
                .ToArray();
            if (mine.Length == 0)
            {
                Emit(plan.RootPid, TreeState.Skipped);
                return items;
            }

            // —— 身份校验层（PRD F3-2）：打开即分类，不一致/不可判 → IdentityChanged 不执行 ——
            var graceful = new List<GraceEntry>();
            var directKill = new List<GraceEntry>();
            foreach (var node in mine)
            {
                var snapshot = node.Snapshot;
                var live = _opener.TryOpen(snapshot.Pid, out var openKind, out var openError);
                if (live == null)
                {
                    if (openKind == LiveOpenKind.Denied)
                    {
                        // 打开受拒：无法打开即无法误杀；拒绝访问按所有者二分（快照 OwnerUser——
                        // 无句柄令牌不可得，PRD F3-7；所有者不可读 fail-safe 归 NeedsElevation）。
                        // 该路径无执行期身份佐证，判定依据在 Reason 注明（cross-review 收口）
                        var (outcome, reason) = AccessDeniedClassifier.Classify(
                            snapshot.OwnerUser, CurrentUserName, openError, "打开进程",
                            ownerSourceNote: "按扫描快照所有者判定");
                        items.Add(SnapshotItem(snapshot, outcome, reason, openError));
                    }
                    else
                    {
                        // 其余打开失败（pid 失效等）= 执行到达前已退出 → Exited
                        items.Add(SnapshotItem(snapshot, ReleaseItemOutcome.Exited, "执行时进程已不存在"));
                    }

                    continue;
                }

                inflight.Add(snapshot.Pid, (snapshot, live));
                if (!live.IdentityMatches(snapshot))
                {
                    // 身份不一致或不可判（快照哨兵/存活侧读取失败）：跳过不执行，防 PID 复用杀错（fail-closed）
                    inflight.Remove(snapshot.Pid);
                    live.Dispose();
                    items.Add(SnapshotItem(snapshot, ReleaseItemOutcome.IdentityChanged,
                        "进程身份与扫描快照不一致（名称或创建时间），已跳过不执行"));
                    continue;
                }

                // —— 执行期窗口自查（裁决⑦；口径 #4 信号不参与释放决策）——
                var hwnds = live.CollectTopLevelWindows();
                if (hwnds != null && hwnds.Count == 0)
                {
                    // 空集 = 确认无可见窗口：直杀列表（跳过优雅等待，PRD F3-4）
                    directKill.Add(new GraceEntry(snapshot, live));
                }
                else
                {
                    // 有窗口（投递列表 = hwnds）；null = 自查失败 → 按有窗口走优雅路径兜底
                    //（窗口列表空集仅不投递，仍等待+转杀，Reason 如实区分未投递）
                    graceful.Add(new GraceEntry(snapshot, live, hwnds));
                }
            }

            if (graceful.Count == 0 && directKill.Count == 0)
            {
                // 全部在校验层出局（Exited/IdentityChanged/Blocked），树无实际动作
                Emit(plan.RootPid, TreeState.Skipped);
                return items;
            }

            if (graceful.Count == 0)
            {
                // 无可见窗口直达边（Pending → Killing）：整树无优雅对象，等待无意义
                Emit(plan.RootPid, TreeState.Killing);
                TerminateAll(directKill, items, inflight);
                Emit(plan.RootPid, TreeState.Done);
                return items;
            }

            // —— Closing：投递 WM_CLOSE；确认无窗口项在本段立即强杀（无消息循环，等待无意义）——
            Emit(plan.RootPid, TreeState.Closing);
            foreach (var entry in graceful)
            {
                foreach (var hwnd in entry.Windows)
                {
                    entry.Live.PostClose(hwnd);
                }
            }

            TerminateAll(directKill, items, inflight);

            // —— Waiting(3s)：优雅项轮询退出（WaitExit(0) 探活 + 有界休眠，总量受 GraceWaitMs 约束）——
            Emit(plan.RootPid, TreeState.Waiting);
            var deadline = Environment.TickCount64 + GraceWaitMs;
            while (graceful.Count > 0)
            {
                for (var i = graceful.Count - 1; i >= 0; i--)
                {
                    var entry = graceful[i];
                    if (!entry.Live.WaitExit(0))
                    {
                        continue;
                    }

                    // 优雅关闭成功（含关闭-退出间窗口极小的自退，机械不可分且对用户无差）
                    graceful.RemoveAt(i);
                    inflight.Remove(entry.Snapshot.Pid);
                    entry.Live.Dispose();
                    items.Add(SnapshotItem(entry.Snapshot, ReleaseItemOutcome.Released,
                        entry.Windows.Count > 0
                            ? "优雅关闭成功（WM_CLOSE 后退出）"
                            : "等待期退出（窗口自查失败，按优雅路径兜底）"));
                }

                if (graceful.Count == 0)
                {
                    break;
                }
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    break;
                }
                Thread.Sleep((int)Math.Min(PollIntervalMs, remaining));
            }

            // —— Killing：3s 超时幸存者转强杀；无幸存者直达 Done ——
            if (graceful.Count > 0)
            {
                Emit(plan.RootPid, TreeState.Killing);
                TerminateAll(graceful, items, inflight);
            }

            Emit(plan.RootPid, TreeState.Done);
            return items;
        }
        catch (Exception ex)
        {
            // 防御性兜底：正常失败均已在上方机械映射；此处保底不丢句柄、不丢项、不击穿整批
            //（在途项逐 pid 补齐结果项，维持 §1.3"每 Pid 恰一项"；仅整树无任何产物时补树级保底项）
            var detail = ex.Message is { Length: > 200 } trimmed ? trimmed[..200] : ex.Message;
            var reason = $"树执行意外终止：{ex.GetType().Name} {detail}";
            foreach (var (_, entry) in inflight)
            {
                entry.Live.Dispose();
                items.Add(SnapshotItem(entry.Snapshot, ReleaseItemOutcome.Blocked, reason));
            }
            inflight.Clear();
            Emit(plan.RootPid, TreeState.Failed);
            if (items.Count == 0)
            {
                var root = plan.Nodes.Count > 0 ? plan.Nodes[0].Snapshot : null;
                items.Add(new ReleaseItemResult(plan.RootPid, root?.Name ?? plan.RootPid.ToString(),
                    root?.ExecutablePath, root?.CommandLine, ReleaseItemOutcome.Blocked, reason));
            }
            return items;
        }
    }

    /// <summary>
    /// 强杀列表内全部进程：成功→ForceKilled；失败按存活复核（已退→Exited；仍存活且拒绝访问→
    /// 执行期令牌名优先、快照 OwnerUser 回退的权限二分，其余错误机械 Blocked+错误码）。
    /// 取消不中断本段（PRD F3-5 法条：进行中的强杀不可打断，防半完成态）。
    /// </summary>
    private void TerminateAll(List<GraceEntry> entries, List<ReleaseItemResult> items,
        Dictionary<int, (ProcessSnapshot Snapshot, ILiveProcess Live)> inflight)
    {
        foreach (var entry in entries)
        {
            var error = entry.Live.Terminate();
            if (error != null && !entry.Live.WaitExit(0))
            {
                // 强杀失败且仍存活：拒绝访问按所有者/令牌二分（PRD F3-7），其余错误机械事实 Blocked
                if (error == Win32Errors.ErrorAccessDenied)
                {
                    var (outcome, reason) = AccessDeniedClassifier.Classify(
                        entry.Live.TryGetTokenUserName() ?? entry.Snapshot.OwnerUser,
                        CurrentUserName, error.Value, "强制结束");
                    items.Add(SnapshotItem(entry.Snapshot, outcome, reason, error));
                }
                else
                {
                    items.Add(SnapshotItem(entry.Snapshot, ReleaseItemOutcome.Blocked,
                        $"强制结束失败（Win32 错误 {error}）", error));
                }
            }
            else
            {
                // 强杀成功；或失败但已退（等待末尾竞态）——如实分列 ForceKilled/Exited
                items.Add(SnapshotItem(entry.Snapshot,
                    error == null ? ReleaseItemOutcome.ForceKilled : ReleaseItemOutcome.Exited,
                    error == null ? "强制结束（TerminateProcess）" : "强制结束前进程已自行退出"));
            }

            inflight.Remove(entry.Snapshot.Pid);
            entry.Live.Dispose();
        }
        entries.Clear();
    }

    /// <summary>快照投影标准形（Pid/Name/Path/CommandLine 四字段单一承载，防增列漏改——DRY 收口）。</summary>
    private static ReleaseItemResult SnapshotItem(
        ProcessSnapshot snapshot, ReleaseItemOutcome outcome, string? reason, int? errorCode = null) =>
        new(snapshot.Pid, snapshot.Name, snapshot.ExecutablePath, snapshot.CommandLine, outcome, reason, errorCode);

    private static ReleaseItemResult SkippedItem(SkippedNode skipped) =>
        new(
            skipped.Snapshot.Pid,
            skipped.Snapshot.Name,
            skipped.Snapshot.ExecutablePath,
            skipped.Snapshot.CommandLine,
            skipped.RootCause == TreeSkipReason.Whitelisted
                ? ReleaseItemOutcome.SkippedWhitelisted
                : ReleaseItemOutcome.SkippedProtected,
            skipped.Detail);

    private void Emit(int rootPid, TreeState state)
    {
        // 工作线程直发，不做线程切换（data-contracts §1.5）；订阅者异常就地隔离——
        // 破坏性主链不因通知通道 misuse 击穿（含 Pending/Failed 发出点，cross-review 收口）
        try
        {
            TreeProgress?.Invoke(rootPid, state);
        }
        catch
        {
            // 订阅者异常不外溢；Core 无日志设施，事件为纯通知（契约 §1.5）
        }
    }

    /// <summary>优雅路径在途项（存活句柄 + 待投递窗口列表；自查失败兜底项列表为空集）。</summary>
    private sealed class GraceEntry(ProcessSnapshot snapshot, ILiveProcess live, IReadOnlyList<nint>? windows = null)
    {
        public ProcessSnapshot Snapshot { get; } = snapshot;
        public ILiveProcess Live { get; } = live;
        public IReadOnlyList<nint> Windows { get; } = windows ?? Array.Empty<nint>();
    }
}
