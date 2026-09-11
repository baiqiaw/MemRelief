using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;
using Windows.Win32;

namespace MemRelief.Core.Tests.Scanner;

// Scanner 类名与命名空间段同名，别名消解（同 ScannerIntegrationTests）
using ScannerImpl = MemRelief.Core.Scanner.Scanner;

// 真机集成冒烟（Windows 本机实跑，门禁常驻）：来源三通道可用性 + 数据健全性 + AC③ 剪枝断言。
// 通道层豁免覆盖率（薄互操作），以真实系统行为验证；条目计数输出留证（AC① 可观测）。

public class SourceProbesIntegrationTests
{
    [Fact]
    public void Run键通道_真机采集成功_条目健全且含条目名()
    {
        var ok = RunKeySourceProbe.TryCollect(out var entries);

        Assert.True(ok, "Run 键通道真机失败（注册表读取异常）");
        Assert.All(entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.EntryName));
            Assert.False(string.IsNullOrWhiteSpace(e.ExecutablePath));
            Assert.True(Path.IsPathRooted(e.ExecutablePath) || e.ExecutablePath.StartsWith('%'), $"路径非 rooted/未展开：{e.ExecutablePath}");
        });
        Console.WriteLine($"[source-evidence] RunKeys: {entries.Count} entries");
        foreach (var e in entries)
        {
            Console.WriteLine($"  [RunKey] {e.EntryName} -> {e.ExecutablePath}");
        }
    }

    [Fact]
    public void 计划任务通道_真机采集成功_仅根与用户自定义文件夹()
    {
        var ok = ScheduledTaskSourceProbe.TryCollectDetailed(out var tasks, out var folderPaths);

        Assert.True(ok, "ITaskService 通道真机失败（COM 连接/枚举异常）");
        // AC③：不采集 \Microsoft 内建任务——访问过的文件夹无 \Microsoft 前缀（剪枝在进入前发生）
        Assert.Contains("\\", folderPaths);
        Assert.DoesNotContain(folderPaths, p =>
            p.Equals(@"\Microsoft", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase));
        Assert.All(tasks, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.EntryName));
            Assert.NotEmpty(t.ExecutablePaths);
            Assert.All(t.ExecutablePaths, p => Assert.False(string.IsNullOrWhiteSpace(p)));
        });
        Console.WriteLine($"[source-evidence] ScheduledTasks: {tasks.Count} tasks, folders visited: {folderPaths.Count}");
        foreach (var t in tasks)
        {
            Console.WriteLine($"  [Task] {t.EntryName} revive={t.RevivesOnLogonBootOrPeriodic} -> {string.Join("; ", t.ExecutablePaths)}");
        }
    }

    [Fact]
    public void 启动文件夹通道_真机采集成功_条目健全()
    {
        var ok = StartupFolderSourceProbe.TryCollect(out var entries);

        Assert.True(ok, "启动文件夹通道真机失败（文件系统/注册表异常）");
        Assert.All(entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.EntryName));
            Assert.False(string.IsNullOrWhiteSpace(e.ExecutablePath));
        });
        Console.WriteLine($"[source-evidence] StartupFolders: {entries.Count} entries");
        foreach (var e in entries)
        {
            Console.WriteLine($"  [Folder] {e.EntryName} -> {e.ExecutablePath}");
        }
    }

    [Fact]
    public void lnk解析_真机构造快捷方式可解析出目标路径()
    {
        // 本机启动文件夹无自然条目（0 个），IShellLinkW 读链以真构造 .lnk 触发（对抗性自测：真实 COM 解析非纸面声明）
        var tempDir = Path.Combine(Path.GetTempPath(), $"T03LinkProbe_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var linkPath = Path.Combine(tempDir, "Notepad.lnk");
            var targetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            CreateShortcutForTest(linkPath, targetPath);

            var resolved = StartupFolderSourceProbe.ResolveLinkTarget(linkPath);

            Assert.NotNull(resolved);
            Assert.Equal(targetPath, resolved, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>测试构造器：WScript.Shell COM 生成真实 .lnk 文件（仅测试链路使用，采集主链路只读）。</summary>
    private static void CreateShortcutForTest(string linkPath, string targetPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        Assert.NotNull(shellType);
        dynamic shell = Activator.CreateInstance(shellType!)!;
        try
        {
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = targetPath;
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    [Fact]
    public void 损坏lnk_解析返回null不抛出()
    {
        // 对抗性自测（修复轮改造：构造物落 %TEMP%，不写系统真实目录）：损坏 .lnk（随机字节）→ 解析 null 跳过
        var corruptLink = Path.Combine(Path.GetTempPath(), $"T03CorruptProbe_{Guid.NewGuid():N}.lnk");
        File.WriteAllBytes(corruptLink, new byte[] { 0x4C, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x01 });
        try
        {
            var resolved = StartupFolderSourceProbe.ResolveLinkTarget(corruptLink);

            Assert.Null(resolved);
        }
        finally
        {
            File.Delete(corruptLink);
        }
    }

    [Fact]
    public void 短名路径_展开回长名()
    {
        // 口径 #3 归一化含 8.3 短名展开（cross-review 补遗）：真机构造短名 → Expand 展开回长名。
        // 卷关闭 8.3 生成时 GetShortPathName 返回原名，展开退化为恒等（同样满足断言）
        var tempDir = Path.Combine(Path.GetTempPath(), $"T03ShortName_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var longPath = Path.Combine(tempDir, "SourceShortNameProbe.txt");
            File.WriteAllBytes(longPath, new byte[] { 0x00 });

            var buffer = new char[1024];
            uint length;
            unsafe
            {
                fixed (char* pathPtr = longPath)
                fixed (char* bufferPtr = buffer)
                {
                    length = PInvoke.GetShortPathName(new Windows.Win32.Foundation.PCWSTR(pathPtr), new Windows.Win32.Foundation.PWSTR(bufferPtr), 1024);
                }
            }
            var shortPath = new string(buffer, 0, (int)length);
            var expanded = ShortPathExpander.Expand(shortPath);

            Assert.Equal(longPath, expanded, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void 展开器_不存在路径原样保留()
    {
        var ghost = @"C:\nonexistent_t03\ghost.exe";
        Assert.Equal(ghost, ShortPathExpander.Expand(ghost));
    }

    [Fact]
    public async Task TakeSnapshot_真机来源通道健康且同目录旁证产出()
    {
        var result = await new ScannerImpl().TakeSnapshot();

        // 三通道真机健康：无 #12/#15 失败记录（有即通道异常，须排查）
        Assert.DoesNotContain(result.Failures, f => f.SignalId is 12 or 15);

        // 同目录旁证（口径 #3，T-03 数据侧）：真机 System32 等目录必然多进程同目录
        var withWitness = result.Snapshots.Count(s => s.Signals.SameDirAlivePids.Count > 0);
        Assert.True(withWitness > 0, "真机快照无任何同目录旁证，#3 归组可疑");

        // 采集段预算：来源三通道并入后仍 ≤2.0s
        Console.WriteLine($"[perf-evidence] TakeSnapshot: {result.ProcessCount} procs, {result.DurationMs}ms, " +
                          $"source-hit procs: {result.Snapshots.Count(s => s.Signals.SourceEntries.Count > 0)}, " +
                          $"revive procs: {result.Snapshots.Count(s => s.Signals.ScheduledTaskWouldRevive == true)}");
        Assert.True(result.DurationMs <= 2000, $"采集段超预算：{result.DurationMs}ms");
    }
}
