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

    // 回归（issue #42）：--mem-mb 0 = 仅挂起形态（帮助文本/README 已声明），参数校验须放行；
    // 就绪事件为正判据——0 被拦截时 child 按参数错误退出（码 1），事件永不置位
    [Fact]
    public void 孤儿构造器_真机_mem_mb_0仅挂起形态放行()
    {
        var exe = ExePath();
        Assert.True(File.Exists(exe), $"构造器未构建：{exe}（gate.ps1 全 sln 构建后应存在）");

        var readyName = $"MemRelief.TestProcs.Ready.{Guid.NewGuid():N}";
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        Process? child = null;
        try
        {
            child = Process.Start(new ProcessStartInfo(exe)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                ArgumentList = { "child", "--mem-mb", "0", "--ready-event", readyName },
            });

            Assert.True(ready.WaitOne(10_000), "child 10s 内未就绪（--mem-mb 0 疑被参数校验拦截）");
            if (child!.HasExited)
            {
                Assert.Fail($"child 在就绪信号后提前退出（退出码 {child.ExitCode}）");
            }
        }
        finally
        {
            if (child is not null)
            {
                TryKillById(child.Id);
            }
        }
    }

    // 非法值负例（越界/非数值，issue #44 扩面）：放行 0 不得放宽上下界或静默回退默认值；
    // finally 兜底：校验若被回归删除，越界值将落入 Sleep(Infinite) 永久挂起，残留进程会击穿同目录断言（同文件 sameDirAliveCount）
    [Theory]
    [InlineData("-1")]
    [InlineData("1025")]
    [InlineData("abc")]
    public void 孤儿构造器_真机_mem_mb非法值仍按参数错误退出(string memMb)
    {
        var exe = ExePath();
        Assert.True(File.Exists(exe), $"构造器未构建：{exe}（gate.ps1 全 sln 构建后应存在）");

        Process? child = null;
        try
        {
            child = Process.Start(new ProcessStartInfo(exe)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                ArgumentList = { "child", "--mem-mb", memMb },
            });
            Assert.True(child!.WaitForExit(10_000), $"--mem-mb {memMb} 10s 内未退出（应按参数错误立即退出）");
            Assert.Equal(1, child.ExitCode);
        }
        finally
        {
            if (child is not null)
            {
                TryKillById(child.Id);
            }
        }
    }

    // parent 侧同口径负例（issue #44 原始症状之一：--mem-mb 非法值曾经 child 拒绝落退出码 2）。
    // 校验前置于 child 启动：参数错误立即退出码 1，不产生 child；若校验被回归删除，
    // parent 将在 ~10s 就绪超时后以退出码 2 结束（自带 child 清理），断言拦截退出码漂移。
    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    public void 孤儿构造器_真机_parent_mem_mb非法值直接参数错误退出(string memMb)
    {
        var exe = ExePath();
        Assert.True(File.Exists(exe), $"构造器未构建：{exe}（gate.ps1 全 sln 构建后应存在）");

        using var parent = Process.Start(new ProcessStartInfo(exe)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            ArgumentList = { "parent", "--mem-mb", memMb },
        });
        Assert.True(parent!.WaitForExit(15_000), $"parent --mem-mb {memMb} 15s 内未退出（应按参数错误立即退出）");
        Assert.Equal(1, parent.ExitCode);
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
