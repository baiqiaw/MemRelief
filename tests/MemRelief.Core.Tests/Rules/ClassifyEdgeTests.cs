using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using Xunit;

namespace MemRelief.Core.Tests.Rules;

// 规则引擎边界与法条单测：保守兜底（法-3）、名单缺失、树合计、旁证互斥、冲突消解、纯函数幂等（法条）
public class ClassifyEdgeTests
{
    private static async Task<Dictionary<int, Classification>> Run(
        ScanResult scan, RulePack? pack = null, WhitelistSnapshot? whitelist = null,
        ClassificationContext? ctx = null)
    {
        var result = await new RulesEngine().Classify(
            scan, whitelist ?? Snap.Whitelist(), pack ?? Snap.Pack(), ctx ?? Snap.Ctx());
        return result.ToDictionary(c => c.Pid);
    }

    [Fact]
    public async Task 白名单命中_完全排除()
    {
        var scan = Snap.Scan(Snap.Clean(1, name: "keepme.exe"), Snap.Clean(2));
        var byId = await Run(scan, whitelist: Snap.Whitelist("keepme.exe"));
        Assert.Equal(Level.Whitelisted, byId[1].Level);
        Assert.Contains(byId[1].Bases, b => b.SignalId == 14);
        Assert.Equal(Level.Recommend, byId[2].Level);
    }

    [Fact]
    public async Task 白名单不区分大小写()
    {
        var scan = Snap.Scan(Snap.Clean(1, name: "KeepMe.exe"));
        var byId = await Run(scan, whitelist: Snap.Whitelist("keepme.exe"));
        Assert.Equal(Level.Whitelisted, byId[1].Level);
    }

    [Fact]
    public async Task 服务进程_失败恢复差异分级()
    {
        var restart = Snap.CleanSignals() with { ServiceName = "SvcA", ServiceRestartOnFailure = true };
        var noRestart = Snap.CleanSignals() with { ServiceName = "SvcB", ServiceRestartOnFailure = false };
        var byId = await Run(Snap.Scan(
            Snap.Clean(1, signals: restart), Snap.Clean(2, signals: noRestart)));
        Assert.Equal(Level.Caution, byId[1].Level);
        Assert.True(byId[1].WouldBeRevived);
        Assert.Contains(byId[1].Bases, b => b.SignalId == 8 && b.Detail.Contains("服务管理器拉起"));
        // v1.2 裁决：服务类（未配置失败恢复）归🚫级带原因
        Assert.Equal(Level.Protected, byId[2].Level);
        Assert.Contains(byId[2].Bases, b => b.SignalId == 8 && b.Detail.Contains("services.msc"));
    }

    [Fact]
    public async Task 保护名单与安全软件_归不推荐级()
    {
        var byId = await Run(Snap.Scan(
            Snap.Clean(1, name: "svchost.exe"),
            Snap.Clean(2, name: "360Tray.exe"),
            Snap.Clean(3, name: "someproc.exe",
                signals: Snap.CleanSignals() with { SignatureStatus = SignatureStatus.ValidNonMicrosoft, SignerName = "360" })));
        Assert.Equal(Level.Protected, byId[1].Level);
        Assert.Equal(Level.Protected, byId[2].Level);
        Assert.Equal(Level.Protected, byId[3].Level); // 安全软件第二通道：签名方匹配
    }

    [Fact]
    public async Task 系统目录微软签名_归不推荐级()
    {
        var sys = Snap.CleanSignals() with
        {
            IsSystemDirectory = true,
            SignatureStatus = SignatureStatus.Microsoft,
        };
        var byId = await Run(Snap.Scan(Snap.Clean(1, name: "winlogon.exe", path: @"C:\Windows\System32\winlogon.exe", signals: sys)));
        Assert.Equal(Level.Protected, byId[1].Level);
        Assert.Contains(byId[1].Bases, b => b.SignalId == 10);
    }

    [Fact]
    public async Task 本工具自身_归不推荐级()
    {
        var byId = await Run(Snap.Scan(Snap.Clean(42, name: "MemRelief.App.exe")), ctx: Snap.Ctx(selfPid: 42));
        Assert.Equal(Level.Protected, byId[42].Level);
        Assert.Contains(byId[42].Bases, b => b.Detail.Contains("本工具自身"));
    }

    [Fact]
    public async Task 计划任务拉起_谨慎且WouldBeRevived()
    {
        var signals = Snap.CleanSignals() with { ScheduledTaskWouldRevive = true };
        var byId = await Run(Snap.Scan(Snap.Clean(1, signals: signals)));
        Assert.Equal(Level.Caution, byId[1].Level);
        Assert.True(byId[1].WouldBeRevived);
        Assert.Contains(byId[1].Bases, b => b.SignalId == 15);
    }

    [Fact]
    public async Task Run键来源_不算拉起_仅作依据展示()
    {
        var signals = Snap.CleanSignals() with
        {
            SourceEntries = new[] { new SourceEntry(SourceType.RunKey, "MyApp") },
        };
        var byId = await Run(Snap.Scan(Snap.Clean(1, bytes: 60 * Snap.Mb, signals: signals)));
        // 来源不构成"会被拉起"，不降级
        Assert.Equal(Level.Recommend, byId[1].Level);
        Assert.Null(byId[1].WouldBeRevived);
        Assert.Contains(byId[1].SourceEntries, e => e.Type == SourceType.RunKey && e.EntryName == "MyApp");
    }

    [Fact]
    public async Task 法3_名单缺失_相关进程不进推荐级()
    {
        var scan = Snap.Scan(Snap.Clean(1), Snap.Clean(2, name: "WeChat.exe"));
        var byId = await Run(scan, pack: RulePack.Empty);
        // 保护名单/常驻名单缺失 → 无法排除保护性判定 → 本应✅/⚠️确认的均不进✅
        Assert.Equal(Level.Caution, byId[1].Level);
        Assert.DoesNotContain(byId.Values, c => c.Level == Level.Recommend);
    }

    [Fact]
    public async Task 法3_采集器级全局失败_全部不进推荐级()
    {
        var scan = Snap.ScanWith(
            new[] { Snap.Clean(1), Snap.Clean(2) },
            new SignalFailure(6, null, FailureKind.CollectorFailed, "TCP 表读取失败"));
        var byId = await Run(scan);
        Assert.DoesNotContain(byId.Values, c => c.Level == Level.Recommend);
    }

    [Fact]
    public async Task 法3_单进程采集失败_仅相关进程降级()
    {
        var scan = Snap.ScanWith(
            new[] { Snap.Clean(1), Snap.Clean(2) },
            new SignalFailure(7, 1, FailureKind.CollectorFailed, "CPU 时间不可读"));
        var byId = await Run(scan);
        Assert.Equal(Level.Caution, byId[1].Level);
        Assert.Equal(Level.Recommend, byId[2].Level);
    }

    [Fact]
    public async Task 树合计_父树含全部后代()
    {
        var child = Snap.Clean(2, ppid: 1, bytes: 30 * Snap.Mb);
        var parent = Snap.Clean(1, bytes: 60 * Snap.Mb);
        var byId = await Run(Snap.Scan(parent, child));
        Assert.Equal(90 * Snap.Mb, byId[1].TreePrivateBytes);
        Assert.Equal(30 * Snap.Mb, byId[2].TreePrivateBytes);
    }

    [Fact]
    public async Task 孤儿群互不为旁证_整群不降级()
    {
        var a = Snap.Clean(1, name: "updater1.exe", path: @"C:\res\updater1.exe",
            signals: Snap.Orphan() with { SameDirAlivePids = new HashSet<int> { 2 } });
        var b = Snap.Clean(2, name: "updater2.exe", path: @"C:\res\updater2.exe", ppid: 1,
            signals: Snap.Orphan() with { SameDirAlivePids = new HashSet<int> { 1 } });
        var byId = await Run(Snap.Scan(a, b));
        Assert.Equal(Level.Recommend, byId[1].Level);
        Assert.Equal(Level.Recommend, byId[2].Level);
    }

    [Fact]
    public async Task 旁证剔除孤儿后非空_仍降级()
    {
        // 同目录 3 个进程：11 孤儿（目标）、12 孤儿（互斥剔除）、10 在用（旁证生效）
        var inUse = Snap.Clean(10, name: "host.exe", path: @"C:\web\host.exe");
        var orphan2 = Snap.Clean(12, name: "helper.exe", path: @"C:\web\helper.exe", ppid: 10,
            signals: Snap.Orphan());
        var target = Snap.Clean(11, name: "crashpad.exe", path: @"C:\web\crashpad.exe",
            signals: Snap.Orphan() with { SameDirAlivePids = new HashSet<int> { 10, 12 } });
        var byId = await Run(Snap.Scan(inUse, orphan2, target));
        Assert.Equal(Level.Caution, byId[11].Level);
    }

    [Fact]
    public async Task 系统目录解析失败_保守归不推荐级()
    {
        var signals = Snap.CleanSignals() with { IsSystemDirectory = null }; // 解析失败 → 视为系统目录（口径 #10）→ 按微软处理（口径 #9）
        var byId = await Run(Snap.Scan(Snap.Clean(1, signals: signals)));
        Assert.Equal(Level.Protected, byId[1].Level);
        Assert.DoesNotContain(byId.Values, c => c.Level == Level.Recommend);
    }

    [Fact]
    public async Task 系统目录命中_无条件归不推荐级()
    {
        // 口径 #9：目录命中「直接按微软处理，不验签」→ 签名状态无关，无条件🚫
        var signals = Snap.CleanSignals() with { IsSystemDirectory = true, SignatureStatus = SignatureStatus.Unsigned };
        var byId = await Run(Snap.Scan(Snap.Clean(1, name: "tool.exe", path: @"C:\Windows\tools\tool.exe", signals: signals)));
        Assert.Equal(Level.Protected, byId[1].Level);
    }

    [Fact]
    public async Task 微软签名_独立通道归不推荐级()
    {
        // 口径 #9「微软 → 🚫候选」+ 附录三通道：非系统目录的微软签名进程同样🚫
        var signals = Snap.CleanSignals() with { SignatureStatus = SignatureStatus.Microsoft };
        var byId = await Run(Snap.Scan(Snap.Clean(1, path: @"D:\tools\app.exe", signals: signals)));
        Assert.Equal(Level.Protected, byId[1].Level);
        Assert.Contains(byId[1].Bases, b => b.SignalId == 9);
    }

    [Fact]
    public async Task 验签不可行_非系统目录也归不推荐级()
    {
        // 口径 #9 兜底「验不了 → 按受保护处理」，不限目录
        var signals = Snap.CleanSignals() with { SignatureStatus = SignatureStatus.Unverifiable };
        var byId = await Run(Snap.Scan(Snap.Clean(1, signals: signals)));
        Assert.Equal(Level.Protected, byId[1].Level);
    }

    [Fact]
    public async Task 验签未执行_推荐候选终局降谨慎()
    {
        // 终局闸门：候选验签漏执行（NotCollected）时✅候选不终态放行
        var signals = Snap.Orphan() with { SignatureStatus = SignatureStatus.NotCollected };
        var byId = await Run(Snap.Scan(Snap.Clean(1, signals: signals)));
        Assert.Equal(Level.Caution, byId[1].Level);
        Assert.Contains(byId[1].Bases, b => b.SignalId == 9 && b.Detail.Contains("验签未执行"));
    }

    [Fact]
    public async Task 路径不可得_保守降级不进推荐()
    {
        // 契约「可空=受保护/系统」：无失败记录时路径 null 不得进✅
        // （with 直赋绕过 Clean 的 path 默认值，构造真实 null 路径）
        var snap = Snap.Clean(1) with { ExecutablePath = null };
        var byId = await Run(Snap.Scan(snap));
        Assert.Equal(Level.Caution, byId[1].Level);
        Assert.Contains(byId[1].Bases, b => b.Detail.Contains("路径不可得"));
    }

    [Fact]
    public async Task 预标_服务与受拒进程_RequiresElevation()
    {
        var svc = Snap.CleanSignals() with { ServiceName = "SvcA", ServiceRestartOnFailure = false };
        var scan = Snap.ScanWith(new[] { Snap.Clean(1, signals: svc), Snap.Clean(2) },
            new SignalFailure(0, 2, FailureKind.AccessDenied, "受拒"));
        var byId = await Run(scan);
        Assert.True(byId[1].RequiresElevation); // 服务预标（T-06 范围）
        Assert.True(byId[2].RequiresElevation); // 受拒预标
        Assert.False(byId[1].Bases.All(b => b.SignalId != 8));
    }

    [Fact]
    public async Task 法条_同输入同输出_纯函数幂等()
    {
        var scan = Snap.Scan(Snap.Clean(1), Snap.Clean(2, name: "WeChat.exe"), Snap.Clean(3, name: "svchost.exe"));
        var engine = new RulesEngine();
        var r1 = await engine.Classify(scan, Snap.Whitelist(), Snap.Pack(), Snap.Ctx());
        var r2 = await engine.Classify(scan, Snap.Whitelist(), Snap.Pack(), Snap.Ctx());
        // record 对集合成员是引用比较，投影为可值比较的结构断言
        var key = (Classification c) => (
            c.Pid, c.Level, c.TreePrivateBytes, c.WouldBeRevived, c.RequiresElevation,
            Bases: c.Bases.Select(b => (b.SignalId, b.Detail)).ToArray());
        Assert.Equal(r1.Select(key), r2.Select(key));
    }

    [Fact]
    public async Task 空扫描_输出空集()
    {
        var result = await new RulesEngine().Classify(
            Snap.Scan(), Snap.Whitelist(), Snap.Pack(), Snap.Ctx());
        Assert.Empty(result);
    }

    // —— 评审补盲区：兜底分支与豁免路径的回归防护 ——

    [Fact]
    public async Task 窗口枚举失败_非UWP_归不推荐级()
    {
        var signals = Snap.CleanSignals() with { HasVisibleWindow = null };
        var byId = await Run(Snap.Scan(Snap.Clean(1, signals: signals)));
        Assert.Equal(Level.Protected, byId[1].Level);
    }

    [Fact]
    public async Task 服务失败恢复配置不可读_保守降级()
    {
        var signals = Snap.CleanSignals() with { ServiceName = "SvcA", ServiceRestartOnFailure = null };
        var byId = await Run(Snap.Scan(Snap.Clean(1, signals: signals)));
        Assert.Equal(Level.Caution, byId[1].Level);
        Assert.DoesNotContain(byId.Values, c => c.Level == Level.Recommend);
    }

    [Fact]
    public async Task CPU差分不可读_保守降级不进推荐()
    {
        var signals = Snap.CleanSignals() with { CpuDeltaSeconds = null };
        var byId = await Run(Snap.Scan(Snap.Clean(1, signals: signals)));
        Assert.Equal(Level.Caution, byId[1].Level);
        Assert.Contains(byId[1].Bases, b => b.SignalId == 7);
    }

    [Fact]
    public async Task 计划任务匹配不可读_保守降级()
    {
        var signals = Snap.CleanSignals() with
        {
            SourceEntries = new[] { new SourceEntry(SourceType.ScheduledTask, "ReviveTask") },
            ScheduledTaskWouldRevive = null,
        };
        var byId = await Run(Snap.Scan(Snap.Clean(1, signals: signals)));
        Assert.Equal(Level.Caution, byId[1].Level);
        Assert.Contains(byId[1].Bases, b => b.SignalId == 15);
    }

    [Fact]
    public async Task 孤儿豁免小体量降级()
    {
        // 口径 #13：孤儿命中豁免小体量降级（此路径曾零覆盖）
        var byId = await Run(Snap.Scan(Snap.Clean(1, bytes: 20 * Snap.Mb, signals: Snap.Orphan())));
        Assert.Equal(Level.Recommend, byId[1].Level);
        Assert.DoesNotContain(byId[1].Bases, b => b.SignalId == 13);
    }

    [Fact]
    public async Task 模式库路径子串与大小写混排命中()
    {
        var byId = await Run(Snap.Scan(Snap.Clean(1, name: "helper.exe",
            path: @"C:\app\CrashReporter\helper.exe")));
        Assert.Equal(Level.Recommend, byId[1].Level);
        // Detail 报告命中的模式名（来自 RulePack 小写基线），大小写混排发生在被匹配路径上
        Assert.Contains(byId[1].Bases, b => b.SignalId == 2 && b.Detail.Contains("crashreporter"));
    }

    [Fact]
    public async Task 旁证集合含自身PID_不误降级()
    {
        var signals = Snap.Orphan() with { SameDirAlivePids = new HashSet<int> { 1, 2 } };
        var other = Snap.Clean(2, name: "other.exe", path: @"C:\apps\other.exe");
        var byId = await Run(Snap.Scan(Snap.Clean(1, signals: signals), other));
        Assert.Equal(Level.Caution, byId[1].Level); // 2 是真实旁证，仍降级
        // 自身 PID 被剔除：仅含自身时无旁证
        var signals2 = Snap.Orphan() with { SameDirAlivePids = new HashSet<int> { 4 } };
        var byId2 = await Run(Snap.Scan(Snap.Clean(4, signals: signals2)));
        Assert.Equal(Level.Recommend, byId2[4].Level);
    }
}
