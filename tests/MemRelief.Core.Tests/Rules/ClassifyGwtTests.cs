using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using Xunit;

namespace MemRelief.Core.Tests.Rules;

// PRD §3.2 R01 全部 GWT（13 条）合成数据单测——T-06 AC#2
// 断言 ID 对应 PRD §3.2 表行序
public class ClassifyGwtTests
{
    private static Dictionary<int, Classification> Classify(
        ScanResult scan, RulePack? pack = null, ClassificationContext? ctx = null)
    {
        var engine = new RulesEngine();
        var result = engine.Classify(
            scan, Snap.Whitelist(), pack ?? Snap.Pack(), ctx ?? Snap.Ctx()).GetAwaiter().GetResult();
        return result.ToDictionary(c => c.Pid);
    }

    // GWT-1 孤儿（父退出）→ ✅，原因含"孤儿进程（父进程已退出）"
    [Fact]
    public void Gwt1_孤儿测试进程_进推荐级()
    {
        var byId = Classify(Snap.Scan(Snap.Clean(1, bytes: 60 * Snap.Mb, signals: Snap.Orphan())));
        Assert.Equal(Level.Recommend, byId[1].Level);
        Assert.Contains(byId[1].Bases, b => b.SignalId == 1 && b.Detail.Contains("孤儿进程（父进程已退出）"));
    }

    // GWT-2 PID 复用（复用者创建时间晚于子）→ ✅（引擎单元级：直接构造 OrphanHint.PidReused）
    [Fact]
    public void Gwt2_PID复用孤儿_进推荐级()
    {
        var byId = Classify(Snap.Scan(Snap.Clean(2, signals: Snap.Orphan(OrphanHint.PidReused))));
        Assert.Equal(Level.Recommend, byId[2].Level);
        Assert.Contains(byId[2].Bases, b => b.SignalId == 1 && b.Detail.Contains("PID 复用"));
    }

    // GWT-3 crashpad 同目录 chrome 存活（旁证降级）→ ⚠️
    [Fact]
    public void Gwt3_同目录存活旁证_降谨慎级()
    {
        var chrome = Snap.Clean(10, name: "chrome.exe", path: @"C:\web\chrome.exe");
        var crashpad = Snap.Clean(11, name: "crashpad.exe", path: @"C:\web\crashpad.exe",
            signals: Snap.Orphan() with { SameDirAlivePids = new HashSet<int> { 10 } });
        var byId = Classify(Snap.Scan(chrome, crashpad));
        Assert.Equal(Level.Caution, byId[11].Level);
        Assert.Contains(byId[11].Bases, b => b.SignalId == 3 && b.Detail.Contains("同目录存在存活进程"));
    }

    // GWT-4 模式库命中且树合计 <50MB → ✅（豁免小体量降级）
    [Fact]
    public void Gwt4_模式库命中_豁免小体量降级()
    {
        var byId = Classify(Snap.Scan(Snap.Clean(4, name: "xxxupdater.exe", bytes: 20 * Snap.Mb)));
        Assert.Equal(Level.Recommend, byId[4].Level);
        Assert.Contains(byId[4].Bases, b => b.SignalId == 2);
    }

    // GWT-5 无可见窗口的用户级应用（树合计 >50MB）→ ✅
    [Fact]
    public void Gwt5_无窗口用户级应用_进推荐级()
    {
        var byId = Classify(Snap.Scan(Snap.Clean(5)));
        Assert.Equal(Level.Recommend, byId[5].Level);
        Assert.Contains(byId[5].Bases, b => b.SignalId == 4 && b.Detail.Contains("无窗口用户级应用"));
    }

    // GWT-6 孤儿 + ESTABLISHED 连接（冲突消解）→ ⚠️，原因含"有活跃网络连接"
    [Fact]
    public void Gwt6_孤儿有活跃连接_冲突消解归谨慎()
    {
        var signals = Snap.Orphan() with { TcpEstablishedCount = 3 };
        var byId = Classify(Snap.Scan(Snap.Clean(6, signals: signals)));
        Assert.Equal(Level.Caution, byId[6].Level);
        Assert.Contains(byId[6].Bases, b => b.SignalId == 6 && b.Detail.Contains("有活跃网络连接"));
        // 孤儿依据仍保留（依据完整可追溯）
        Assert.Contains(byId[6].Bases, b => b.SignalId == 1);
    }

    // GWT-7 扫描窗口内 CPU 差分 >1s → ⚠️
    [Fact]
    public void Gwt7_CPU差分显著_归谨慎级()
    {
        var signals = Snap.CleanSignals() with { CpuDeltaSeconds = 1.5 };
        var byId = Classify(Snap.Scan(Snap.Clean(7, signals: signals)));
        Assert.Equal(Level.Caution, byId[7].Level);
        Assert.Contains(byId[7].Bases, b => b.SignalId == 7 && b.Detail.Contains("疑似任务进行中"));
    }

    // GWT-8 非豁免无窗口应用树合计 <50MB → ⚠️"占用过小"
    [Fact]
    public void Gwt8_非豁免小体量_降谨慎级()
    {
        var byId = Classify(Snap.Scan(Snap.Clean(8, bytes: 20 * Snap.Mb)));
        Assert.Equal(Level.Caution, byId[8].Level);
        Assert.Contains(byId[8].Bases, b => b.SignalId == 13 && b.Detail.Contains("占用过小"));
    }

    // GWT-9 UWP 包进程 → ⚠️（特例保守），不进✅
    [Fact]
    public void Gwt9_UWP包进程_保守归谨慎()
    {
        var signals = Snap.CleanSignals() with
        {
            IsUwpPackage = true,
            HasVisibleWindow = null, // UWP 窗口挂 ApplicationFrameHost，枚举不到
        };
        var byId = Classify(Snap.Scan(Snap.Clean(9, name: "Calculator.exe",
            path: @"C:\Program Files\WindowsApps\Calc\Calc.exe", signals: signals)));
        Assert.Equal(Level.Caution, byId[9].Level);
        Assert.DoesNotContain(byId[9].Bases, b => b.SignalId == 10); // WindowsApps 不触发系统目录链
    }

    // GWT-10 常驻应用名单命中 → ⚠️"疑似常驻应用"
    [Fact]
    public void Gwt10_常驻名单命中_归谨慎级()
    {
        var byId = Classify(Snap.Scan(Snap.Clean(12, name: "WeChat.exe")));
        Assert.Equal(Level.Caution, byId[12].Level);
        Assert.Contains(byId[12].Bases, b => b.SignalId == 5 && b.Detail.Contains("疑似常驻应用"));
    }

    // GWT-11 仅常规进程（有窗口，在用）→ 零可推荐输出
    [Fact]
    public void Gwt11_无可推荐项_输出空推荐集()
    {
        var visible = Snap.CleanSignals() with { HasVisibleWindow = true };
        var byId = Classify(Snap.Scan(Snap.Clean(13, name: "notepad.exe", signals: visible)));
        Assert.DoesNotContain(byId.Values, c => c.Level == Level.Recommend);
        Assert.DoesNotContain(byId.Values, c => c.Level == Level.Caution);
        Assert.All(byId.Values, c => Assert.Equal(Level.Protected, c.Level));
    }

    // GWT-12 PPL（打开受拒）→ 🚫"受保护进程"，不进✅
    [Fact]
    public void Gwt12_PPL受拒_归不推荐级()
    {
        var ppl = Snap.Clean(14, name: "pplproc.exe", path: null,
            signals: Snap.CleanSignals() with { SignatureStatus = SignatureStatus.Unverifiable });
        var scan = Snap.ScanWith(new[] { ppl },
            new SignalFailure(0, 14, FailureKind.AccessDenied, "OpenProcess 受拒"));
        var byId = Classify(scan);
        Assert.Equal(Level.Protected, byId[14].Level);
        Assert.Contains(byId[14].Bases, b => b.Detail.Contains("受保护进程"));
    }

    // GWT-13 提权进程（部分元数据不可读，非 PPL）→ ⚠️"元数据不可读"，不进✅
    [Fact]
    public void Gwt13_元数据不可读_谨慎且不进推荐()
    {
        var elevated = Snap.Clean(15, path: null,
            signals: Snap.CleanSignals() with { SignatureStatus = SignatureStatus.NotCollected });
        var scan = Snap.ScanWith(new[] { elevated },
            new SignalFailure(0, 15, FailureKind.Unreadable, "路径不可读（提权进程）"));
        var byId = Classify(scan);
        Assert.Equal(Level.Caution, byId[15].Level);
        Assert.Contains(byId[15].Bases, b => b.SignalId == 0 && b.Detail.Contains("元数据不可读"));
    }
}
