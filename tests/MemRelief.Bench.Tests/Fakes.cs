using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using MemRelief.Core.Scanner;

namespace MemRelief.Bench.Tests;

/// <summary>跨替身共享的调用序列记录器：断言四步链编排顺序。</summary>
internal sealed class CallLog
{
    private readonly object _gate = new();
    private readonly List<string> _entries = [];

    public void Add(string entry)
    {
        lock (_gate) _entries.Add(entry);
    }

    public List<string> Snapshot()
    {
        lock (_gate) return [.. _entries];
    }
}

/// <summary>scanner 替身：记录调用、可配段延迟与定制快照，供编排/计时归位断言。</summary>
internal sealed class FakeScanner(CallLog log) : IScanner
{
    public int SnapshotDelayMs { get; set; }
    public int VerifyDelayMs { get; set; }
    public Func<ScanResult>? SnapshotFactory { get; set; }

    public Task<ScanResult> TakeSnapshot()
    {
        log.Add("TakeSnapshot");
        if (SnapshotDelayMs > 0)
        {
            Thread.Sleep(SnapshotDelayMs);
        }
        return Task.FromResult(SnapshotFactory?.Invoke() ?? SampleResults.Empty());
    }

    public Task<ScanResult> CollectSignatures(ScanResult snapshot, ISet<int> candidatePids)
    {
        log.Add("CollectSignatures");
        if (VerifyDelayMs > 0)
        {
            Thread.Sleep(VerifyDelayMs);
        }
        return Task.FromResult(snapshot);
    }

    public Task<MemoryOverview> SampleOverview() =>
        throw new NotSupportedException("验证台四步链不含概览采样");
}

/// <summary>rules 替身：记录调用与延迟；Classify 输出可配。</summary>
internal sealed class FakeRules(CallLog log) : IRulesEngine
{
    public int ClassifyDelayMs { get; set; }
    public IReadOnlyList<Classification>? ClassifyOutput { get; set; }

    public ISet<int> CandidateIds(ScanResult scan)
    {
        log.Add("CandidateIds");
        return new HashSet<int>();
    }

    public Task<IReadOnlyList<Classification>> Classify(
        ScanResult scan, WhitelistSnapshot whitelist, RulePack rulePack, ClassificationContext context)
    {
        log.Add("Classify");
        if (ClassifyDelayMs > 0)
        {
            Thread.Sleep(ClassifyDelayMs);
        }
        return Task.FromResult(ClassifyOutput ?? Array.Empty<Classification>());
    }

    public IReadOnlyList<QueryResult> Query(
        ScanResult scan, IReadOnlyList<Classification> classifications, string? name, int? pid) =>
        throw new NotSupportedException("验证台不消费查询");
}

/// <summary>固定样本：合成快照与分类，供渲染断言。</summary>
internal static class SampleResults
{
    public static ScanResult Empty() => new(
        DateTime.UtcNow, 0, 0,
        Array.Empty<ProcessSnapshot>(), Array.Empty<SignalFailure>());

    public static ScanResult TwoProcesses() => new(
        DateTime.UtcNow, 2, 5,
        [
            new ProcessSnapshot(100, 1, "orphan.exe", @"C:\app\orphan.exe",
                DateTime.UtcNow, 60 * 1024 * 1024,
                Signals: new SignalSet(OrphanHint.ParentDead, HasVisibleWindow: false,
                    IsSystemDirectory: false, SignatureStatus: SignatureStatus.ValidNonMicrosoft)),
            new ProcessSnapshot(200, 1, "guarded.exe", @"C:\Windows\guarded.exe",
                DateTime.UtcNow, 10 * 1024 * 1024,
                Signals: new SignalSet(IsSystemDirectory: true,
                    SignatureStatus: SignatureStatus.Microsoft)),
        ],
        [new SignalFailure(100, null, FailureKind.AccessDenied, "打开受拒")]);

    public static IReadOnlyList<Classification> TwoClassifications() =>
    [
        new Classification(100, Level.Recommend,
            [new Basis(1, "孤儿进程（父进程已退出）")],
            60 * 1024 * 1024, null,
            [new SourceEntry(SourceType.RunKey, "orphan")], false),
        new Classification(200, Level.Protected,
            [new Basis(10, "系统目录进程")],
            10 * 1024 * 1024, null, [], false),
    ];
}
