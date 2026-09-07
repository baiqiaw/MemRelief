using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using Xunit;

namespace MemRelief.Core.Tests.Rules;

// 候选预筛单测：排除且仅排除「无论签名结果如何终局必🚫」者（rules.md §4.1 谓词）
public class CandidateIdsTests
{
    private static ISet<int> Run(params ProcessSnapshot[] snapshots) =>
        new RulesEngine().CandidateIds(Snap.Scan(snapshots));

    [Fact]
    public void 潜在推荐_孤儿进程_入选()
    {
        var ids = Run(Snap.Clean(1, signals: Snap.Orphan()));
        Assert.Contains(1, ids);
    }

    [Fact]
    public void 潜在推荐_无窗口用户级应用_入选()
    {
        var ids = Run(Snap.Clean(1));
        Assert.Contains(1, ids);
    }

    [Fact]
    public void 潜在推荐_残留模式命中_入选()
    {
        var ids = Run(Snap.Clean(1, name: "updater.exe"));
        Assert.Contains(1, ids);
    }

    [Fact]
    public void UWP包进程_入选()
    {
        // UWP 终态仅受签名影响（⚠️/🚫），窗口/目录值不影响终局 → 预筛不排除
        var signals = new SignalSet(IsUwpPackage: true, HasVisibleWindow: null, IsSystemDirectory: null);
        var ids = Run(Snap.Clean(1, path: @"C:\Program Files\WindowsApps\pkg\app.exe", signals: signals));
        Assert.Contains(1, ids);
    }

    [Fact]
    public void 服务_可被拉起_入选()
    {
        var signals = Snap.CleanSignals() with { ServiceName = "SvcA", ServiceRestartOnFailure = true };
        var ids = Run(Snap.Clean(1, signals: signals));
        Assert.Contains(1, ids);
    }

    [Fact]
    public void 系统目录_排除()
    {
        var signals = Snap.CleanSignals() with { IsSystemDirectory = true };
        var ids = Run(Snap.Clean(1, signals: signals));
        Assert.DoesNotContain(1, ids);
    }

    [Fact]
    public void 系统目录不可读_保守排除()
    {
        // 非 UWP 且 null：与 Classify 同向保守视为系统目录
        var signals = Snap.CleanSignals() with { IsSystemDirectory = null };
        var ids = Run(Snap.Clean(1, signals: signals));
        Assert.DoesNotContain(1, ids);
    }

    [Fact]
    public void 有可见窗口_排除()
    {
        var signals = Snap.CleanSignals() with { HasVisibleWindow = true };
        var ids = Run(Snap.Clean(1, signals: signals));
        Assert.DoesNotContain(1, ids);
    }

    [Fact]
    public void 窗口枚举失败_保守排除()
    {
        var signals = Snap.CleanSignals() with { HasVisibleWindow = null };
        var ids = Run(Snap.Clean(1, signals: signals));
        Assert.DoesNotContain(1, ids);
    }

    [Fact]
    public void 打开受拒_排除()
    {
        var scan = Snap.ScanWith(new[] { Snap.Clean(1) },
            new SignalFailure(0, 1, FailureKind.AccessDenied, "PPL 受拒"));
        var ids = new RulesEngine().CandidateIds(scan);
        Assert.DoesNotContain(1, ids);
    }

    [Fact]
    public void 服务_不恢复_排除()
    {
        // 终局🚫（服务类无恢复归🚫），签名状态不影响 → 预筛排除
        var signals = Snap.CleanSignals() with { ServiceName = "SvcA", ServiceRestartOnFailure = false };
        var ids = Run(Snap.Clean(1, signals: signals));
        Assert.DoesNotContain(1, ids);
    }

    [Fact]
    public void 纯函数幂等_同输入同输出()
    {
        var scan = Snap.Scan(
            Snap.Clean(1),
            Snap.Clean(2, signals: Snap.CleanSignals() with { IsSystemDirectory = true }),
            Snap.Clean(3, signals: Snap.Orphan()));
        var engine = new RulesEngine();
        var r1 = engine.CandidateIds(scan);
        var r2 = engine.CandidateIds(scan);
        Assert.Equal(r1.OrderBy(x => x), r2.OrderBy(x => x));
    }

    [Fact]
    public void 空扫描_空集()
    {
        Assert.Empty(new RulesEngine().CandidateIds(Snap.Scan()));
    }

    [Fact]
    public async Task 契约时序_预筛驱动验签回填后可达推荐级()
    {
        // scanner §4.3 四步时序的引擎级验证：
        // TakeSnapshot（NotCollected）→ CandidateIds → CollectSignatures（对候选回填）→ Classify
        var orphan = Snap.Clean(1, signals: Snap.Orphan() with { SignatureStatus = SignatureStatus.NotCollected });
        var sysDir = Snap.Clean(2, signals: Snap.CleanSignals() with { IsSystemDirectory = true });
        var scan = Snap.Scan(orphan, sysDir);

        var candidates = new RulesEngine().CandidateIds(scan);
        Assert.Contains(1, candidates);   // 潜在✅必须入选，否则终局闸门降级
        Assert.DoesNotContain(2, candidates);

        // 模拟 CollectSignatures：仅对候选回填报验结果
        var filled = scan.Snapshots
            .Select(p => candidates.Contains(p.Pid)
                ? p with { Signals = p.Signals with { SignatureStatus = SignatureStatus.ValidNonMicrosoft } }
                : p)
            .ToArray();
        var scanFilled = scan with { Snapshots = filled };

        var result = await new RulesEngine().Classify(scanFilled, Snap.Whitelist(), Snap.Pack(), Snap.Ctx());
        var byId = result.ToDictionary(c => c.Pid);
        Assert.Equal(Level.Recommend, byId[1].Level); // 预筛正确驱动 → ✅可达
        Assert.Equal(Level.Protected, byId[2].Level); // 系统目录不验签也终局🚫
    }

    [Fact]
    public async Task 不变量_预筛排除等价于中性判定终局受保护()
    {
        // 护栏：「签名无关的终局🚫」知识在 CandidateIds 谓词与 ClassifyOne 各写一份
        //（rules.md §4.1），任一侧口径改动破坏等价即红灯——防漂移静默降级
        //（过排除→✅候选被闸门压⚠️；过包含→白验签）。
        // 中性入参 = 空白名单 + 空名单包 + SelfPid 不命中：剥离名单通道，仅剩 ScanResult 可判定。
        var scan = Snap.ScanWith(
            new[]
            {
                Snap.Clean(1),                                                          // 潜在✅ → 入选
                Snap.Clean(2, signals: Snap.Orphan()),                                  // 孤儿 → 入选
                Snap.Clean(3, signals: new SignalSet(IsUwpPackage: true)),              // UWP → 入选
                Snap.Clean(4, signals: Snap.CleanSignals() with { ServiceName = "S", ServiceRestartOnFailure = true }), // 服务恢复 → 入选
                Snap.Clean(5, name: "WeChat.exe"),                                      // 名单命中（空包不生效）→ 入选
                Snap.Clean(6, signals: Snap.CleanSignals() with { IsSystemDirectory = true }),   // 系统目录 → 排除
                Snap.Clean(7, signals: Snap.CleanSignals() with { IsSystemDirectory = null }),   // 目录不可读 → 排除
                Snap.Clean(8, signals: Snap.CleanSignals() with { HasVisibleWindow = true }),    // 有窗口 → 排除
                Snap.Clean(9, signals: Snap.CleanSignals() with { HasVisibleWindow = null }),    // 窗口不可读 → 排除
                Snap.Clean(10, signals: Snap.CleanSignals() with { ServiceName = "S", ServiceRestartOnFailure = false }), // 服务不恢复 → 排除
                Snap.Clean(11),                                                         // AccessDenied → 排除
            },
            new SignalFailure(0, 11, FailureKind.AccessDenied, "PPL 受拒"));

        var engine = new RulesEngine();
        var candidates = engine.CandidateIds(scan);
        var classified = await engine.Classify(
            scan, Snap.Whitelist(), RulePack.Empty, new ClassificationContext(-1, null));
        var byId = classified.ToDictionary(c => c.Pid);

        foreach (var p in scan.Snapshots)
        {
            Assert.Equal(!candidates.Contains(p.Pid), byId[p.Pid].Level == Level.Protected);
        }
    }
}
