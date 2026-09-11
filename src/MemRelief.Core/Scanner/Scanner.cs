using System.Diagnostics;
using MemRelief.Core.Contracts;

namespace MemRelief.Core.Scanner;

/// <summary>
/// scanner 模块组合实现（IScanner，spec §5）：原生枚举与 WMI 命令行通道重叠执行（编排层设计决策，
/// 缩短采集关键路径），命令行合并为纯函数，装配收口于 SnapshotAssembler（scanner.md §4.1）。
/// 采集段预算 ≤2.0s（system §7 分解）；异步不阻塞调用方。
/// </summary>
public sealed class Scanner : IScanner
{
    private readonly NativeProcessEnumerator _native = new();
    private readonly WmiCommandLineSource _wmi = new();
    private readonly SignatureCache _signatureCache;
    private readonly Func<string, SignatureVerdict>? _verifierOverride;

    /// <summary>组合根默认构造；签名验证缓存随实例存活（scanner.md §6：唯一跨快照可变状态例外）。</summary>
    public Scanner()
        : this(new SignatureCache(), verifier: null)
    {
    }

    /// <summary>测试注入构造：验签替身（internal，白盒测试承载接线断言）。</summary>
    internal Scanner(SignatureCache signatureCache, Func<string, SignatureVerdict>? verifier)
    {
        _signatureCache = signatureCache;
        _verifierOverride = verifier;
    }

    /// <summary>签名验证缓存（诊断/测试观测用）。</summary>
    internal SignatureCache SignatureCache => _signatureCache;

    /// <summary>来源三通道时限（同 WMI 通道先例同级，data-contracts §2 T-01 裁决⑤）：ITaskService 经
    /// RPC 依赖 Task Scheduler 服务，服务挂起时 COM 调用可无限阻塞——超时按三通道全失败降级，
    /// 经 SignalFailure #12/#15 链承接（保守方向），防击穿采集段 ≤2.0s 硬预算。</summary>
    internal static readonly TimeSpan SourceChannelTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>采集快照（口径#1/#13 数据侧 + 基础字段 + T-02 活动信号五通道 + T-03 来源三通道 + 失败记录框架）。</summary>
    public async Task<ScanResult> TakeSnapshot()
    {
        var stopwatch = Stopwatch.StartNew();
        // WMI 与原生枚举重叠（WMI 冷启动 COM 初始化较慢）；通道超时/失败→命令行全量 null（裁决⑤）
        var commandLinesTask = _wmi.QueryCommandLinesAsync();
        // 来源三通道与原生枚举重叠（T-03）：注册表/ITaskService COM/文件系统互不相依，单通道失败独立降级
        var sourcesTask = CollectSourceInputsAsync();

        // CPU 差分窗口起点随枚举捕获（grilling 裁决②：窗口=TakeSnapshot 采集段）
        var (rows, takenAtUtc) = _native.Enumerate();

        var sources = await sourcesTask.ConfigureAwait(false);
        // 活动信号四通道（窗口/TCP/服务/目录解析）+ 来源通道 + CPU 差分终点二次采样，均在采集段内完成
        var signals = CollectSignals(rows, sources);

        var commandLines = await commandLinesTask.ConfigureAwait(false);
        var merged = MergeCommandLines(rows, commandLines);

        stopwatch.Stop();
        return SnapshotAssembler.Assemble(merged, signals, takenAtUtc, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>活动信号采集编排（T-02）：四通道互不相依顺序执行 + CPU 终点二次采样；单通道失败以全局 failure 降级不击穿。</summary>
    private SignalInputs CollectSignals(IReadOnlyList<RawProcess> rows, SourceInputs sources)
    {
        var pids = new HashSet<int>(rows.Select(r => r.Pid));
        var windowOk = WindowProbe.TryCollectVisiblePids(pids, out var visible);
        var tcpOk = TcpProbe.TryCountEstablishedByPid(pids, out var tcp);
        var serviceOk = ServiceProbe.TryCollectServices(out var services);
        var (prefixes, uwpPrefix, directoryFailed) = SystemDirectoryResolver.Resolve();
        var cpuDeltas = _native.ReadCpuDeltas(rows);

        return new SignalInputs(
            visible, !windowOk,
            tcp, !tcpOk,
            services, !serviceOk,
            prefixes, uwpPrefix, directoryFailed,
            cpuDeltas,
            Sources: sources);
    }

    /// <summary>来源三通道采集（T-03，口径 #12/#15）：互不相依，各通道失败独立降级（Try* 收口 false）；
    /// 整体施加通道时限防 COM/RPC 挂起（超时=三通道全失败，保守降级）。</summary>
    private static async Task<SourceInputs> CollectSourceInputsAsync()
    {
        var collect = Task.Run(CollectSourceInputs);
        try
        {
            return await collect.WaitAsync(SourceChannelTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 挂起型失败：弃任务保底观察异常防 UnobservedTaskException，三旗标走既有失败链
            _ = collect.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return new SourceInputs(
                Array.Empty<RunKeySource>(), RunKeyFailed: true,
                Array.Empty<ScheduledTaskSource>(), ScheduledTaskFailed: true,
                Array.Empty<StartupFolderSource>(), StartupFolderFailed: true);
        }
    }

    /// <summary>来源三通道采集（T-03，口径 #12/#15）：互不相依，各通道失败独立降级（Try* 收口 false）。</summary>
    private static SourceInputs CollectSourceInputs()
    {
        var runKeyOk = RunKeySourceProbe.TryCollect(out var runKeys);
        var taskOk = ScheduledTaskSourceProbe.TryCollect(out var tasks);
        var folderOk = StartupFolderSourceProbe.TryCollect(out var folders);
        return new SourceInputs(runKeys, !runKeyOk, tasks, !taskOk, folders, !folderOk);
    }

    /// <summary>候选验签（两阶段协议采集侧，口径#9 仅候选执行 + 路径+mtime 缓存，issue #11/T-04）。
    /// 仅改写候选行签名字段，其余行/失败记录/时长原样保留；编排方在 CandidateIds（rules）之后调用（scanner.md §4.3）。
    /// 线程池执行不阻塞调用方（PRD §3.4 扫描异步）；快照为一次性产物，本方法不重试、不做存在性预检（时点口径）。</summary>
    public Task<ScanResult> CollectSignatures(ScanResult snapshot, ISet<int> candidatePids)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidatePids);
        return Task.Run(() => CollectSignaturesCore(snapshot, candidatePids));
    }

    private ScanResult CollectSignaturesCore(ScanResult snapshot, ISet<int> candidatePids)
    {
        if (candidatePids.Count == 0)
        {
            return snapshot;
        }

        var verify = _verifierOverride ?? SignatureVerifier.Verify;
        var snapshots = snapshot.Snapshots;
        var updated = new ProcessSnapshot[snapshots.Count];
        for (var i = 0; i < snapshots.Count; i++)
        {
            var candidate = snapshots[i];
            updated[i] = candidatePids.Contains(candidate.Pid)
                ? candidate with { Signals = MergeSignatureSignals(candidate, verify) }
                : candidate;
        }
        return snapshot with { Snapshots = updated };
    }

    /// <summary>单候选验签（口径#9 四分支）：系统目录核心命中不验签按微软（UWP WindowsApps 例外，v1.2 裁决）；
    /// 路径不可得=验不了按受保护处理（路径失败已由编号 100 记录，不重复触验签）；
    /// 其余经 WinVerifyTrust（路径+mtime 缓存；文件已消失 → mtime 1601 零值哨兵、读取异常 → MinValue 哨兵，
    /// 均独立成键不污染正常条目，失败结论由缓存工厂兜底保守化）。单候选异常不击穿整批（逐候选隔离）。
    /// 已知边界（v1 接受）：网络共享路径 mtime 读取可能按网络超时阻塞，无时限护栏（本地路径为主的目标机场景）。</summary>
    private SignalSet MergeSignatureSignals(ProcessSnapshot candidate, Func<string, SignatureVerdict> verify)
    {
        var path = candidate.ExecutablePath;
        if (path is null)
        {
            return candidate.Signals with { SignatureStatus = SignatureStatus.Unverifiable, SignerName = null };
        }
        if (candidate.Signals.IsSystemDirectory == true && !candidate.Signals.IsUwpPackage)
        {
            return candidate.Signals with { SignatureStatus = SignatureStatus.Microsoft, SignerName = null };
        }
        DateTime mtime;
        try
        {
            mtime = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            // mtime 不可得（属性受限/路径形态异常，非文件消失场景）：MinValue 哨兵独立成键，缓存仍生效
            mtime = DateTime.MinValue;
        }
        var verdict = _signatureCache.GetOrAdd(path, mtime, verify);
        return candidate.Signals with { SignatureStatus = verdict.Status, SignerName = verdict.SignerName };
    }

    /// <summary>内存概览三数值采样（NtQuerySystemInformation 优先、PDH 三计数器兜底、GlobalMemoryStatusEx 终底）。</summary>
    public Task<MemoryOverview> SampleOverview()
    {
        // T-05 实装（issue #9）；通道梯与降级语义收口于 MemoryOverviewSampler（纯函数单测承载）
        var sampler = new MemoryOverviewSampler();
        return Task.FromResult(sampler.Sample());
    }

    /// <summary>命令行合并（纯函数）：按 pid 补全；commandLines=null=通道级失败→全量保持 null 无记录（裁决⑤）。</summary>
    /// <remarks>已知边界（v1 接受）：两通道重叠执行存在 pid 复用 TOCTOU 窗口——pid 在枚举后、WMI 查询前退出并被复用时，
    /// 命令行可能错配到新进程。契约无配对键通道（创建时间双键配对归后续版本裁决），T-01 issue 证据评论登记。</remarks>
    internal static List<RawProcess> MergeCommandLines(List<RawProcess> rows, Dictionary<int, string?>? commandLines)
    {
        if (commandLines is null)
        {
            return rows;
        }
        var merged = new List<RawProcess>(rows.Count);
        foreach (var row in rows)
        {
            if (commandLines.TryGetValue(row.Pid, out var commandLine))
            {
                merged.Add(row with { CommandLine = commandLine });
            }
            else
            {
                merged.Add(row);
            }
        }
        return merged;
    }
}
