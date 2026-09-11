using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;

namespace MemRelief.App.Tests.TestDoubles;

/// <summary>
/// IScanner 测试替身（App.Tests 共用）：记录调用序、可注入各步行为与挂起点。
/// 仅测试程序集内使用，非生产代码。
/// </summary>
public sealed class FakeScanner : IScanner
{
    public static ScanResult DefaultSnapshot { get; } = new(
        TakenAtUtc: new DateTime(2026, 9, 11, 8, 0, 0, DateTimeKind.Utc),
        ProcessCount: 1,
        DurationMs: 10,
        Snapshots:
        [
            new ProcessSnapshot(100, 0, "a.exe", @"C:\apps\a.exe",
                new DateTime(2026, 9, 11, 7, 0, 0, DateTimeKind.Utc), 60_000_000),
        ],
        Failures: []);

    public List<string> Calls { get; } = [];

    public int TakeSnapshotCount { get; private set; }

    public int SampleOverviewCount { get; private set; }

    public Func<Task<ScanResult>> OnTakeSnapshot { get; set; } = () => Task.FromResult(DefaultSnapshot);

    public Func<ScanResult, ISet<int>, Task<ScanResult>> OnCollectSignatures { get; set; } =
        (s, _) => Task.FromResult(s);

    public Func<Task<MemoryOverview>> OnSampleOverview { get; set; } = () => Task.FromResult(
        new MemoryOverview(16L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024,
            10L * 1024 * 1024 * 1024, 20L * 1024 * 1024 * 1024, null, MemoryOverviewSource.NtQuery));

    public ISet<int>? ReceivedCandidates { get; private set; }

    public async Task<ScanResult> TakeSnapshot()
    {
        Calls.Add(nameof(TakeSnapshot));
        TakeSnapshotCount++;
        return await OnTakeSnapshot().ConfigureAwait(false);
    }

    public async Task<ScanResult> CollectSignatures(ScanResult snapshot, ISet<int> candidatePids)
    {
        Calls.Add(nameof(CollectSignatures));
        ReceivedCandidates = candidatePids;
        return await OnCollectSignatures(snapshot, candidatePids).ConfigureAwait(false);
    }

    public async Task<MemoryOverview> SampleOverview()
    {
        Calls.Add(nameof(SampleOverview));
        SampleOverviewCount++;
        return await OnSampleOverview().ConfigureAwait(false);
    }
}
