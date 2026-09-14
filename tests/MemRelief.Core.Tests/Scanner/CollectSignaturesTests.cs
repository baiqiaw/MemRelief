using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;
using Xunit;

namespace MemRelief.Core.Tests.Scanner;

// Scanner 类名与命名空间段同名，别名消解
using ScannerImpl = MemRelief.Core.Scanner.Scanner;

// CollectSignatures 接线单测（口径 #9 两阶段协议采集侧）：候选筛选、短路分支、缓存接入、
// 原样保留语义。需要真实 WinVerifyTrust 的四分支证据归 SignatureVerificationIntegrationTests。
public class CollectSignaturesTests
{
    private static readonly DateTime TakenAt = new(2026, 9, 11, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>可控验签替身：返回预设结论并计数，承载"是否真的调了验签"的断言。</summary>
    private static (ScannerImpl Scanner, List<string> Paths) ScannerWithFake(
        SignatureVerdict verdict, out Func<int> callCount)
    {
        var paths = new List<string>();
        var calls = 0;
        var scanner = new ScannerImpl(new SignatureCache(), path =>
        {
            Interlocked.Increment(ref calls);
            lock (paths) { paths.Add(path); }
            return verdict;
        });
        callCount = () => Volatile.Read(ref calls);
        return (scanner, paths);
    }

    private static ScanResult ScanOf(params ProcessSnapshot[] snapshots) =>
        new(TakenAt, snapshots.Length, 1234, snapshots,
            [new SignalFailure(7, 4242, FailureKind.Unreadable, "既有失败记录（应原样保留）")]);

    private static ProcessSnapshot Snap(int pid, string? path, bool? isSystemDirectory = false, bool isUwp = false) =>
        new(pid, 1, "app.exe", path, TakenAt.AddMinutes(-1), 60 * 1024 * 1024,
            Signals: new SignalSet(IsSystemDirectory: isSystemDirectory, IsUwpPackage: isUwp));

    [Fact]
    public async Task 非候选行_签名状态不被触碰()
    {
        var (scanner, paths) = ScannerWithFake(new SignatureVerdict(SignatureStatus.Microsoft, null), out var calls);
        var scan = ScanOf(Snap(101, @"C:\app\a.exe"), Snap(202, @"C:\app\b.exe"));

        var result = await scanner.CollectSignatures(scan, new HashSet<int> { 202 }); // 仅 202 是候选

        Assert.Equal(1, calls()); // 只有候选 202 触发验签
        Assert.Equal([@"C:\app\b.exe"], paths);
        Assert.Equal(SignatureStatus.NotCollected, result.Snapshots[0].Signals.SignatureStatus); // 101 原样
        Assert.Equal(SignatureStatus.Microsoft, result.Snapshots[1].Signals.SignatureStatus);
    }

    [Fact]
    public async Task 候选不在快照中_跳过不抛()
    {
        var (scanner, _) = ScannerWithFake(new SignatureVerdict(SignatureStatus.Unsigned, null), out var calls);

        var result = await scanner.CollectSignatures(ScanOf(Snap(101, @"C:\app\a.exe")), new HashSet<int> { 999 });

        Assert.Equal(0, calls());
        Assert.Single(result.Snapshots);
    }

    [Fact]
    public async Task 空候选集_原样返回()
    {
        var (scanner, _) = ScannerWithFake(new SignatureVerdict(SignatureStatus.Microsoft, null), out var calls);
        var scan = ScanOf(Snap(101, @"C:\app\a.exe"));

        var result = await scanner.CollectSignatures(scan, new HashSet<int>());

        Assert.Equal(0, calls());
        Assert.Equal(scan, result);
    }

    [Fact]
    public async Task 路径不可得候选_Unverifiable且不触验签()
    {
        var (scanner, _) = ScannerWithFake(new SignatureVerdict(SignatureStatus.Microsoft, null), out var calls);

        var result = await scanner.CollectSignatures(ScanOf(Snap(101, null)), new HashSet<int> { 101 });

        // 验不了=按受保护处理（口径 #9 兜底）；路径失败已由编号 100 记录，不重复触验签
        Assert.Equal(0, calls());
        Assert.Equal(SignatureStatus.Unverifiable, result.Snapshots[0].Signals.SignatureStatus);
    }

    [Fact]
    public async Task 系统目录候选_不验签直接按Microsoft()
    {
        var (scanner, _) = ScannerWithFake(new SignatureVerdict(SignatureStatus.Unsigned, null), out var calls);
        // 路径不存在也能命中 Microsoft → 证明未触文件（真验签对不存在文件只会落 Unverifiable）
        var scan = ScanOf(Snap(101, @"C:\Windows\System32\nonexistent-a1b2c3.exe", isSystemDirectory: true));

        var result = await scanner.CollectSignatures(scan, new HashSet<int> { 101 });

        Assert.Equal(0, calls());
        Assert.Equal(SignatureStatus.Microsoft, result.Snapshots[0].Signals.SignatureStatus);
        Assert.Null(result.Snapshots[0].Signals.SignerName); // 仅 ValidNonMicrosoft 携带（契约 §1.1）
    }

    [Fact]
    public async Task UWP系统目录候选_不受短路豁免_走真实验签()
    {
        // WindowsApps 不触发"系统目录→按微软"链路（PRD v1.2 裁决）：替身返回 Unsigned = 验签确被调用
        var (scanner, _) = ScannerWithFake(new SignatureVerdict(SignatureStatus.Unsigned, null), out var calls);
        var scan = ScanOf(Snap(101, @"C:\nonexistent-uwp\b.exe", isSystemDirectory: true, isUwp: true));

        var result = await scanner.CollectSignatures(scan, new HashSet<int> { 101 });

        Assert.Equal(1, calls());
        Assert.Equal(SignatureStatus.Unsigned, result.Snapshots[0].Signals.SignatureStatus);
    }

    [Fact]
    public async Task 替身结论连同SignerName回填到信号()
    {
        var (scanner, _) = ScannerWithFake(new SignatureVerdict(SignatureStatus.ValidNonMicrosoft, "ACME"), out _);

        var result = await scanner.CollectSignatures(ScanOf(Snap(101, @"C:\app\a.exe")), new HashSet<int> { 101 });

        Assert.Equal(SignatureStatus.ValidNonMicrosoft, result.Snapshots[0].Signals.SignatureStatus);
        Assert.Equal("ACME", result.Snapshots[0].Signals.SignerName);
    }

    [Fact]
    public async Task 同键二次扫描_缓存生效不重验()
    {
        var (scanner, _) = ScannerWithFake(new SignatureVerdict(SignatureStatus.Unsigned, null), out var calls);
        var scan = ScanOf(Snap(101, @"C:\app\a.exe"));

        await scanner.CollectSignatures(scan, new HashSet<int> { 101 });
        await scanner.CollectSignatures(scan, new HashSet<int> { 101 });

        Assert.Equal(1, calls()); // 第二次走缓存
        Assert.Equal(1, scanner.SignatureCache.Count);
    }

    [Fact]
    public async Task mtime变更_同实例跨扫描缓存失效重验()
    {
        var dir = Directory.CreateTempSubdirectory("memrelief-sigcache-");
        try
        {
            var path = Path.Combine(dir.FullName, "a.exe");
            File.WriteAllBytes(path, [1, 2, 3]);
            var (scanner, _) = ScannerWithFake(new SignatureVerdict(SignatureStatus.Unsigned, null), out var calls);
            var scan = ScanOf(Snap(101, path));

            await scanner.CollectSignatures(scan, new HashSet<int> { 101 });
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1)); // 文件被替换 → mtime 变化
            await scanner.CollectSignatures(scan, new HashSet<int> { 101 });

            Assert.Equal(2, calls()); // mtime 变更 = 新键，不得复用旧结论
            Assert.Equal(2, scanner.SignatureCache.Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task 快照其余字段_原样保留()
    {
        var (scanner, _) = ScannerWithFake(new SignatureVerdict(SignatureStatus.Unsigned, null), out _);
        var scan = ScanOf(Snap(101, @"C:\app\a.exe"), Snap(202, null));
        scan = scan with { TakenAtUtc = TakenAt, DurationMs = 1234 };

        var result = await scanner.CollectSignatures(scan, new HashSet<int> { 101 });

        Assert.Equal(TakenAt, result.TakenAtUtc);
        Assert.Equal(1234, result.DurationMs);
        Assert.Equal(2, result.ProcessCount);
        Assert.Single(result.Failures); // 既有失败记录原样（本例替身结论 Unsigned=通道健康，探针不触发；系统性失效场景见下方探针用例）
        Assert.Same(scan.Snapshots[1], result.Snapshots[1]); // 非候选行同实例
    }

    // —— 验签通道系统性失效探针（issue #35-1，2026-09-14 TL 裁决补探针：全候选 Unverifiable → 全局 SignalFailure）——

    [Fact]
    public async Task 全候选Unverifiable_系统性失效_追加全局SignalFailure()
    {
        var (scanner, _) = ScannerWithFake(new SignatureVerdict(SignatureStatus.Unverifiable, null), out _);
        var scan = ScanOf(Snap(101, @"C:\app\a.exe"), Snap(202, @"C:\app\b.exe"));

        var result = await scanner.CollectSignatures(scan, new HashSet<int> { 101, 202 });

        var global = Assert.Single(result.Failures, f => !f.Pid.HasValue);
        Assert.Equal(9, global.SignalId);
        Assert.Equal(FailureKind.CollectorFailed, global.Kind);
        Assert.Contains("系统性失效", global.Detail);
    }

    [Fact]
    public async Task 全候选路径不可得Unverifiable_未经通道判定_不触发探针()
    {
        // 路径不可得候选走 MergeSignatureSignals 短路分支直接 Unverifiable，验签通道从未被调用——
        // 不能作为"通道失效"证据（否则误指引排障方向查 cryptsvc，真实根因在路径读取）
        var (scanner, _) = ScannerWithFake(new SignatureVerdict(SignatureStatus.Unverifiable, null), out var calls);
        var scan = ScanOf(Snap(101, null), Snap(202, null));

        var result = await scanner.CollectSignatures(scan, new HashSet<int> { 101, 202 });

        Assert.Equal(0, calls());
        Assert.DoesNotContain(result.Failures, f => !f.Pid.HasValue);
    }

    [Fact]
    public async Task 全Unverifiable含系统目录直判_Microsoft不遮蔽探针()
    {
        // 系统目录直判不经 WinVerifyTrust 通道，不能证明通道健康——不得遮蔽系统性失效判定
        var (scanner, _) = ScannerWithFake(new SignatureVerdict(SignatureStatus.Unverifiable, null), out _);
        var scan = ScanOf(
            Snap(101, @"C:\Windows\System32\nonexistent-x1y2z3.exe", isSystemDirectory: true),
            Snap(202, @"C:\app\b.exe"));

        var result = await scanner.CollectSignatures(scan, new HashSet<int> { 101, 202 });

        Assert.Contains(result.Failures, f => !f.Pid.HasValue);
    }

    [Fact]
    public async Task 通道有健康结论_混有Unverifiable_不触发探针()
    {
        // 任一 Valid/Invalid/Unsigned 结论 = 通道工作正常（个别 Unverifiable 是文件级现象非通道级）
        var (scanner, _) = ScannerWithFake(new SignatureVerdict(SignatureStatus.Unsigned, null), out _);
        var scan = ScanOf(Snap(101, @"C:\app\a.exe"), Snap(202, null)); // 202 路径不可得 → Unverifiable

        var result = await scanner.CollectSignatures(scan, new HashSet<int> { 101, 202 });

        Assert.DoesNotContain(result.Failures, f => !f.Pid.HasValue);
    }

    [Fact]
    public async Task 空引用入参_同步抛ArgumentNull()
    {
        var (scanner, _) = ScannerWithFake(new SignatureVerdict(SignatureStatus.Unsigned, null), out _);
        var scan = ScanOf(Snap(101, @"C:\app\a.exe"));

        await Assert.ThrowsAsync<ArgumentNullException>(() => scanner.CollectSignatures(null!, new HashSet<int> { 101 }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => scanner.CollectSignatures(scan, null!));
    }
}
