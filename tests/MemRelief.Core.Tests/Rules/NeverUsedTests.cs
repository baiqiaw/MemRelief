using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;

namespace MemRelief.Core.Tests.Rules;

/// <summary>
/// 「从未使用」口径 #16 判定测试（#59，PRD F1 处理逻辑增补）：自启动累计 CPU &lt;5s 的
/// 无窗口应用与第三方服务进 ✅（豁免小体量降级）；微软/系统目录服务仍 🚫、自动重启服务仍 ⚠️、
/// CPU 累计不可读依据不成立（保守兜底照旧）。断言 Basis.SignalId 对应口径表编号。
/// </summary>
public class NeverUsedTests
{
    private static readonly DateTime T = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<Classification> ClassifyOneAsync(ProcessSnapshot snapshot) =>
        (await new RulesEngine().Classify(Snap.Scan(snapshot), Snap.Whitelist(), Snap.Pack(), Snap.Ctx()))
        .Single(c => c.Pid == snapshot.Pid);

    private static ProcessSnapshot WindowlessApp(int pid, double? cpuTotal, long bytes = 10 * Snap.Mb) => new(
        pid, 0, "bgapp.exe", @"C:\apps\bgapp.exe", T, bytes,
        Signals: Snap.CleanSignals() with { CpuTotalSeconds = cpuTotal });

    private static ProcessSnapshot Service(
        int pid,
        double? cpuTotal,
        bool? restartOnFailure = false,
        SignatureStatus signature = SignatureStatus.ValidNonMicrosoft,
        bool? isSystemDirectory = false) => new(
        pid, 0, "svc.exe", @"C:\Program Files\Vendor\svc.exe", T, 30 * Snap.Mb,
        Signals: new SignalSet(
            HasVisibleWindow: false,
            CpuDeltaSeconds: 0.0,
            CpuTotalSeconds: cpuTotal,
            ServiceName: "VendorService",
            ServiceRestartOnFailure: restartOnFailure,
            IsSystemDirectory: isSystemDirectory,
            SignatureStatus: signature));

    [Fact]
    public async Task 从未使用的无窗口应用_小体量进推荐级_豁免50MB降级()
    {
        var c = await ClassifyOneAsync(WindowlessApp(1, cpuTotal: 1.2, bytes: 10 * Snap.Mb)); // 树合计 <50MB

        Assert.Equal(Level.Recommend, c.Level);
        Assert.Contains(c.Bases, b => b.SignalId == 16 && b.Detail.Contains("从未使用"));
        Assert.DoesNotContain(c.Bases, b => b.SignalId == 13);   // 小体量降级不触发
        Assert.DoesNotContain(c.Bases, b => b.SignalId == 4);    // 不与"无窗口用户级应用"重复计据
    }

    [Fact]
    public async Task 累计干活的应用_维持小体量降级_口径不变()
    {
        var c = await ClassifyOneAsync(WindowlessApp(1, cpuTotal: 6.0, bytes: 10 * Snap.Mb));

        Assert.Equal(Level.Caution, c.Level);
        Assert.Contains(c.Bases, b => b.SignalId == 4);
        Assert.Contains(c.Bases, b => b.SignalId == 13);         // 旧口径：小体量降 ⚠️
    }

    [Fact]
    public async Task 没用过的第三方服务_直接进推荐级()
    {
        var c = await ClassifyOneAsync(Service(1, cpuTotal: 0.5));

        Assert.Equal(Level.Recommend, c.Level);
        Assert.Contains(c.Bases, b => b.SignalId == 16 && b.Detail.Contains("VendorService")
            && b.Detail.Contains("services.msc"));
        Assert.True(c.RequiresElevation);                        // 服务预标保持（释放需管理员）
    }

    [Fact]
    public async Task 微软签名服务_仍不推荐_不挂推荐语调依据()
    {
        var c = await ClassifyOneAsync(Service(1, cpuTotal: 0.5, signature: SignatureStatus.Microsoft));

        Assert.Equal(Level.Protected, c.Level);
        Assert.Contains(c.Bases, b => b.SignalId == 9);
        Assert.DoesNotContain(c.Bases, b => b.SignalId == 16);   // 终局🚫者维持 Basis(8) 口径（#59 评审修复）
    }

    [Fact]
    public async Task 系统目录服务_仍不推荐_不挂推荐语调依据()
    {
        var c = await ClassifyOneAsync(Service(1, cpuTotal: 0.5, isSystemDirectory: true));

        Assert.Equal(Level.Protected, c.Level);
        Assert.Contains(c.Bases, b => b.SignalId == 10);
        Assert.DoesNotContain(c.Bases, b => b.SignalId == 16);
    }

    [Fact]
    public async Task 自动重启服务_维持谨慎级会被拉起()
    {
        var c = await ClassifyOneAsync(Service(1, cpuTotal: 0.5, restartOnFailure: true));

        Assert.Equal(Level.Caution, c.Level);
        Assert.Equal(true, c.WouldBeRevived);
        Assert.Contains(c.Bases, b => b.SignalId == 8);
    }

    [Fact]
    public async Task CPU累计不可读_依据不成立_维持旧服务口径()
    {
        var c = await ClassifyOneAsync(Service(1, cpuTotal: null));

        Assert.Equal(Level.Protected, c.Level);                  // v1.2 裁决：不恢复服务归🚫
        Assert.DoesNotContain(c.Bases, b => b.SignalId == 16);
    }

    [Fact]
    public async Task CPU差分不可读_从未使用服务不进推荐级()
    {
        // 累计 CPU<5s 但采集段差分不可读（null）→ compromised 拦截授予，终态 ⚠️ 可见（非 Unmatched 漏单）
        var snapshot = Service(1, cpuTotal: 0.5) with
        {
            Signals = ((SignalSet)Service(1, cpuTotal: 0.5).Signals!) with { CpuDeltaSeconds = null },
        };
        var c = await ClassifyOneAsync(snapshot);

        Assert.NotEqual(Level.Recommend, c.Level);
        Assert.NotEqual(Level.Unmatched, c.Level);
        Assert.Contains(c.Bases, b => b.SignalId == 7 && b.Detail.Contains("不可读"));
    }

    [Fact]
    public async Task 累计CPU恰等于阈值_不算从未使用()
    {
        var c = await ClassifyOneAsync(WindowlessApp(1, cpuTotal: 5.0, bytes: 10 * Snap.Mb));

        Assert.Equal(Level.Caution, c.Level);                    // 边界：5.0 不满足 <5，走旧口径降 ⚠️
        Assert.Contains(c.Bases, b => b.SignalId == 13);
    }
}

/// <summary>候选预筛（CandidateIds）服务豁免（#59）：没用过（累计 CPU&lt;5s）的不恢复服务保留进验签面。</summary>
public class NeverUsedCandidateTests
{
    private static readonly DateTime T = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);

    private static ProcessSnapshot Service(int pid, double? cpuTotal, bool restart = false) => new(
        pid, 0, "svc.exe", @"C:\Program Files\Vendor\svc.exe", T, 30 * Snap.Mb,
        Signals: new SignalSet(
            HasVisibleWindow: false,
            CpuTotalSeconds: cpuTotal,
            ServiceName: "VendorService",
            ServiceRestartOnFailure: restart,
            IsSystemDirectory: false,
            SignatureStatus: SignatureStatus.NotCollected));

    [Fact]
    public void 没用过的不恢复服务_入选候选()
    {
        var ids = new RulesEngine().CandidateIds(Snap.Scan(Service(1, cpuTotal: 0.5)));
        Assert.Contains(1, ids);
    }

    [Fact]
    public void 累计干活的不恢复服务_维持排除()
    {
        var ids = new RulesEngine().CandidateIds(Snap.Scan(Service(1, cpuTotal: 6.0)));
        Assert.DoesNotContain(1, ids);
    }

    [Fact]
    public void CPU不可读的不恢复服务_维持排除()
    {
        var ids = new RulesEngine().CandidateIds(Snap.Scan(Service(1, cpuTotal: null)));
        Assert.DoesNotContain(1, ids);
    }
}
