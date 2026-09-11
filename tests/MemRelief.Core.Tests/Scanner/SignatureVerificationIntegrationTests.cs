using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;
using Xunit;
using Xunit.Abstractions;

namespace MemRelief.Core.Tests.Scanner;

// Scanner 类名与命名空间段同名，别名消解
using ScannerImpl = MemRelief.Core.Scanner.Scanner;

// 验签真机集成测试（issue #11 AC）：真实 WinVerifyTrust 四分支证据 + 缓存 ≤0.5s 真机计时 +
// Access Denied/被删/被锁文件异常路径。合成快照（验签读文件不触进程，pid 可虚构），文件副本入临时目录。
public class SignatureVerificationIntegrationTests
{
    private static readonly DateTime TakenAt = new(2026, 9, 11, 8, 0, 0, DateTimeKind.Utc);
    private readonly ITestOutputHelper _output;

    public SignatureVerificationIntegrationTests(ITestOutputHelper output) => _output = output;

    private static string ExePath() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "MemRelief.TestProcs", "bin", "Debug", "net10.0", "MemRelief.TestProcs.exe"));

    /// <summary>微软签名源文件（内嵌 Authenticode 签名的系统组件按序回退；Windows 开发机恒有 explorer.exe）。
    /// 注意：notepad/cmd/powershell 等为 catalog 签名（无内嵌证书表），WTD_CHOICE_FILE 通道验为 Unsigned，不可作微软分支源。</summary>
    private static string MicrosoftSignedSource()
    {
        var windir = Environment.GetEnvironmentVariable("windir")!;
        var candidates = new[]
        {
            Path.Combine(windir, "explorer.exe"),
            Path.Combine(Environment.SystemDirectory, "shell32.dll"),
            Path.Combine(Environment.SystemDirectory, "msi.dll"),
        };
        return candidates.First(File.Exists);
    }

    private static string NewTempDir() => Directory.CreateTempSubdirectory("memrelief-sigtest-").FullName;

    private static ProcessSnapshot Snap(int pid, string path) =>
        new(pid, 1, "app.exe", path, TakenAt.AddMinutes(-1), 60 * 1024 * 1024,
            Signals: new SignalSet(IsSystemDirectory: false));

    private static ScanResult ScanOf(params ProcessSnapshot[] snapshots) =>
        new(TakenAt, snapshots.Length, 1234, snapshots, []);

    private static ISet<int> Pids(IEnumerable<ProcessSnapshot> snapshots) =>
        snapshots.Select(s => s.Pid).ToHashSet();

    /// <summary>临时目录清理：AV/索引器短暂持有新复制文件句柄时容忍漏删（不遮蔽断言失败，残留归系统临时目录回收）。</summary>
    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---------- AC1：四分支真机证据 ----------
    // 判别力说明：已删除/被锁/ACL 拒绝三用例的判定价值依赖"健康机器上其他文件能得到非 Unverifiable 结论"作对照——
    // 微软签名副本_验为Microsoft 用例即承担该对照（其通过证明通道整体可用，Unverifiable 三用例才是有区分度的证据）。

    [Fact]
    public async Task 微软签名副本_验为Microsoft()
    {
        var dir = NewTempDir();
        try
        {
            // 系统组件副本：非系统目录路径 → 不触发短路，真走 WinVerifyTrust；签名随字节复制保留
            var path = Path.Combine(dir, "ms-copy.exe");
            File.Copy(MicrosoftSignedSource(), path);

            var snapshots = new[] { Snap(101, path) };
            var result = await new ScannerImpl().CollectSignatures(ScanOf(snapshots), Pids(snapshots));

            Assert.Equal(SignatureStatus.Microsoft, result.Snapshots[0].Signals.SignatureStatus);
            Assert.Null(result.Snapshots[0].Signals.SignerName);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task 无签名程序_验为Unsigned()
    {
        var dir = NewTempDir();
        try
        {
            var source = ExePath();
            Assert.True(File.Exists(source), $"构造器未构建：{source}（gate.ps1 全 sln 构建后应存在）");
            var path = Path.Combine(dir, "unsigned-copy.exe");
            File.Copy(source, path);

            var snapshots = new[] { Snap(102, path) };
            var result = await new ScannerImpl().CollectSignatures(ScanOf(snapshots), Pids(snapshots));

            Assert.Equal(SignatureStatus.Unsigned, result.Snapshots[0].Signals.SignatureStatus);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task 篡改微软签名副本_验为Invalid()
    {
        var dir = NewTempDir();
        try
        {
            // 字节翻转（正文段，远离证书表）→ Authenticode 摘要不符 → TRUST_E_BAD_DIGEST
            var path = Path.Combine(dir, "tampered-copy.exe");
            File.Copy(MicrosoftSignedSource(), path);
            var bytes = File.ReadAllBytes(path);
            bytes[bytes.Length / 8] ^= 0xFF;
            await File.WriteAllBytesAsync(path, bytes);

            var snapshots = new[] { Snap(103, path) };
            var result = await new ScannerImpl().CollectSignatures(ScanOf(snapshots), Pids(snapshots));

            Assert.Equal(SignatureStatus.Invalid, result.Snapshots[0].Signals.SignatureStatus);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ---------- AC 兜底分支：验不了 → Unverifiable（rules 按受保护处理） ----------

    [Fact]
    public async Task 已删除文件_验为Unverifiable()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "vanished.exe");
            File.WriteAllBytes(path, [1, 2, 3]);
            File.Delete(path); // 快照后、验签前文件消失（时点口径不重试）

            var snapshots = new[] { Snap(104, path) };
            var result = await new ScannerImpl().CollectSignatures(ScanOf(snapshots), Pids(snapshots));

            Assert.Equal(SignatureStatus.Unverifiable, result.Snapshots[0].Signals.SignatureStatus);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task 被独占锁定文件_验为Unverifiable()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "locked-copy.exe");
            File.Copy(MicrosoftSignedSource(), path);

            var snapshots = new[] { Snap(105, path) };
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) // 独占锁
            {
                var result = await new ScannerImpl().CollectSignatures(ScanOf(snapshots), Pids(snapshots));
                Assert.Equal(SignatureStatus.Unverifiable, result.Snapshots[0].Signals.SignatureStatus);
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task ACL拒绝读取_验为Unverifiable()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "denied-copy.exe");
            File.Copy(MicrosoftSignedSource(), path);

            var identity = WindowsIdentity.GetCurrent().User!;
            var security = new FileSecurity(path, AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.Read, AccessControlType.Deny));
            new FileInfo(path).SetAccessControl(security);
            try
            {
                var snapshots = new[] { Snap(106, path) };
                var result = await new ScannerImpl().CollectSignatures(ScanOf(snapshots), Pids(snapshots));
                Assert.Equal(SignatureStatus.Unverifiable, result.Snapshots[0].Signals.SignatureStatus);
            }
            finally
            {
                // 恢复 DACL 后清理（文件属主始终可改 DACL）
                security.RemoveAccessRule(
                    new FileSystemAccessRule(identity, FileSystemRights.Read, AccessControlType.Deny));
                new FileInfo(path).SetAccessControl(security);
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ---------- 对抗：mtime 读取异常的单候选隔离（Deny-ReadAttributes 触发 GetLastWriteTimeUtc 抛 UnauthorizedAccess） ----------

    [Fact]
    public async Task mtime属性受限_单候选隔离且同行不受牵连()
    {
        var dir = NewTempDir();
        try
        {
            var denied = Path.Combine(dir, "attr-denied.exe");
            File.Copy(MicrosoftSignedSource(), denied);
            var normal = Path.Combine(dir, "normal.exe");
            File.Copy(MicrosoftSignedSource(), normal);

            var identity = WindowsIdentity.GetCurrent().User!;
            var security = new FileSecurity(denied, AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.ReadAttributes, AccessControlType.Deny));
            new FileInfo(denied).SetAccessControl(security);
            try
            {
                var snapshots = new[] { Snap(107, denied), Snap(108, normal) };
                var result = await new ScannerImpl().CollectSignatures(ScanOf(snapshots), Pids(snapshots));

                // 107：mtime 读取抛异常 → 哨兵键隔离，候选仍得结论（状态随打开权限而定，非 NotCollected 即达隔离语义）
                Assert.NotEqual(SignatureStatus.NotCollected, result.Snapshots[0].Signals.SignatureStatus);
                // 108：正常同行不受 107 异常牵连，完整走通验签
                Assert.Equal(SignatureStatus.Microsoft, result.Snapshots[1].Signals.SignatureStatus);
            }
            finally
            {
                security.RemoveAccessRule(
                    new FileSystemAccessRule(identity, FileSystemRights.ReadAttributes, AccessControlType.Deny));
                new FileInfo(denied).SetAccessControl(security);
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ---------- AC2：缓存生效后重复扫描验签 ≤0.5s（真机计时） ----------
    // 冷路径为真实 WinVerifyTrust（30 文件，微软签名/无签名副本混合），断言缓存生效后热路径 ≤500ms
    // （热路径仅 mtime 读取+字典查找，不触 WinVerifyTrust）。

    [Fact]
    public async Task 缓存生效后重复验签_真机计时不超过500ms()
    {
        var dir = NewTempDir();
        try
        {
            // 30 个候选：微软签名副本 ×15 + 无签名副本 ×15（混合结论，缓存键各不相同）
            var msSource = MicrosoftSignedSource();
            var unsignedSource = ExePath();
            Assert.True(File.Exists(unsignedSource), $"构造器未构建：{unsignedSource}（gate.ps1 全 sln 构建后应存在）");
            var snapshots = new List<ProcessSnapshot>();
            for (var i = 0; i < 30; i++)
            {
                var path = Path.Combine(dir, $"cand-{i}.exe");
                File.Copy(i % 2 == 0 ? msSource : unsignedSource, path);
                snapshots.Add(Snap(200 + i, path));
            }
            var candidates = Pids(snapshots);
            var scanner = new ScannerImpl();
            var scan = ScanOf(snapshots.ToArray());

            var cold = Stopwatch.StartNew();
            await scanner.CollectSignatures(scan, candidates);
            cold.Stop();

            var hot = Stopwatch.StartNew();
            var cached = await scanner.CollectSignatures(scan, candidates);
            hot.Stop();

            _output.WriteLine($"候选数={candidates.Count} 冷验签={cold.ElapsedMilliseconds}ms " +
                              $"热验签(缓存生效)={hot.ElapsedMilliseconds}ms 缓存条目={scanner.SignatureCache.Count}");
            Assert.True(hot.ElapsedMilliseconds <= 500,
                $"缓存生效后重复验签 {hot.ElapsedMilliseconds}ms 超出 500ms（AC2）");
            Assert.Equal(candidates.Count, scanner.SignatureCache.Count);
            Assert.Equal(SignatureStatus.Microsoft, cached.Snapshots[0].Signals.SignatureStatus);
            Assert.Equal(SignatureStatus.Unsigned, cached.Snapshots[1].Signals.SignatureStatus);
        }
        finally
        {
            Cleanup(dir);
        }
    }
}
