using System.Diagnostics;
using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;

namespace MemRelief.Core.Tests.Scanner;

// Scanner 类名与命名空间段同名，别名消解
using ScannerImpl = MemRelief.Core.Scanner.Scanner;

// T-20 孤儿测试进程构造器真机端到端（AC：被 R01/R03 真机验收复用——本测试即首个消费者）：
// 构造 → 扫描 → 断言构造规格全部满足（父退出/>50MB/无窗口/无连接/非服务/非 UWP）→ 清理。
public class TestProcsIntegrationTests
{
    private static string ExePath() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "MemRelief.TestProcs", "bin", "Debug", "net10.0", "MemRelief.TestProcs.exe"));

    [Fact]
    public async Task 孤儿构造器_真机_子进程满足构造规格()
    {
        // 构造规格 8 条件断言覆盖 6 条（父退出/>50MB/无窗口/无连接/非服务/非 UWP/非系统目录/同目录无其他存活）；
        // 「不在常驻名单」由进程名 MemRelief.TestProcs 构造性满足（resident-apps.json 精确名不命中）
        var exe = ExePath();
        Assert.True(File.Exists(exe), $"构造器未构建：{exe}（gate.ps1 全 sln 构建后应存在）");

        var outPidFile = Path.Combine(Path.GetTempPath(), $"mrtproc-{Guid.NewGuid():N}.txt");
        Process? parent = null;
        var childPid = 0;
        try
        {
            parent = Process.Start(new ProcessStartInfo(exe)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                ArgumentList = { "parent", "--mem-mb", "60", "--out-pid", outPidFile },
            });

            // 就绪判据 = parent 退出（WriteAllLines 在退出前完成，规避 pid 文件半写与 ParentDead 双竞态）
            Assert.True(parent!.WaitForExit(15_000), "parent 15s 内未退出（child 未就绪或超时）");

            childPid = int.Parse(File.ReadAllLines(outPidFile)[0]);
            Assert.NotEqual(0, childPid);

            // 全局采集失败会使断言空洞通过（字段默认值≠已核实）——健康真机不应出现
            var scan = await new ScannerImpl().TakeSnapshot();
            Assert.DoesNotContain(scan.Failures, f => f.Pid is null && f.Kind == FailureKind.CollectorFailed);
            Assert.DoesNotContain(scan.Failures, f => f.Pid == childPid);

            var orphan = scan.Snapshots.SingleOrDefault(s => s.Pid == childPid);
            Assert.NotNull(orphan);
            Assert.Equal(OrphanHint.ParentDead, orphan.Signals.OrphanHint);        // 父退出
            Assert.False(orphan.Signals.HasVisibleWindow == true);                 // 无可见窗口
            Assert.Equal(0, orphan.Signals.TcpEstablishedCount);                   // 无 ESTABLISHED
            Assert.Null(orphan.Signals.ServiceName);                               // 非服务
            Assert.False(orphan.Signals.IsUwpPackage);                             // 非 UWP
            Assert.True(orphan.PrivateCommittedBytes > 50 * 1024 * 1024,           // >50MB
                $"私有提交 {orphan.PrivateCommittedBytes} 未达 50MB");
            Assert.False(SignalRules.IsUnderAnyPrefix(
                orphan.ExecutablePath, [Environment.GetEnvironmentVariable("windir")!]));  // 非系统目录

            // 同可执行目录无其他存活进程（口径 #3 同目录条件；SameDirAlivePids 采集归 T-03，此处按快照直算）
            var directory = Path.GetDirectoryName(orphan.ExecutablePath);
            var sameDirAliveCount = scan.Snapshots.Count(s =>
                s.Pid != childPid &&
                s.ExecutablePath is not null &&
                string.Equals(Path.GetDirectoryName(s.ExecutablePath), directory, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, sameDirAliveCount);
        }
        finally
        {
            // 清理：杀 child 与 parent（构造进程均挂起/短命，尽力而为）
            if (childPid != 0)
            {
                TryKillById(childPid);
            }
            if (parent is not null)
            {
                TryKillById(parent.Id);
            }
            if (File.Exists(outPidFile))
            {
                File.Delete(outPidFile);
            }
        }
    }

    private static void TryKillById(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch
        {
            // 已退出/已无权限：清理尽力而为
        }
    }
}
