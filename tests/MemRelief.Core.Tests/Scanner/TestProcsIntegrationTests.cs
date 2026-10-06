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

    // 回归（issue #55）：带值参数提供但缺值（位于末尾）或空串 = 参数错误退出码 1，不与「未提供」混同。
    // 原症状：--out-pid 缺值被按未提供处理 → parent 码 0 正常退出，错误配置被掩盖；
    // 空串 → WriteAllLines("") 异常崩溃。校验前置于 child 启动（同 #44 口径），参数错误不产生 child。
    // 兜底口径：守卫若被回归删除，缺值形态 parent 码 0 正常退出且 child 成无 pid 孤儿（无 pid 文件正是被测缺陷，
    // 无法自动回收）——由首测同目录存活断言连锁暴露，手工按映像名回收（README「停止」段）
    [Theory]
    [InlineData(null)]   // 缺值：--out-pid 位于参数末尾
    [InlineData("")]     // 空串：--out-pid ""
    public void 孤儿构造器_真机_parent_out_pid缺值或空串按参数错误退出(string? value)
    {
        var exe = ExePath();
        Assert.True(File.Exists(exe), $"构造器未构建：{exe}（gate.ps1 全 sln 构建后应存在）");

        var psi = new ProcessStartInfo(exe) { CreateNoWindow = true, UseShellExecute = false };
        psi.ArgumentList.Add("parent");
        psi.ArgumentList.Add("--mem-mb");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("--out-pid");
        if (value is not null)
        {
            psi.ArgumentList.Add(value);
        }
        using var parent = Process.Start(psi);
        Assert.True(parent!.WaitForExit(15_000), "parent 15s 内未退出（--out-pid 缺值/空串应按参数错误立即退出）");
        Assert.Equal(1, parent.ExitCode);
    }

    // child 侧同口径（--ready-event 缺值原被按未提供处理 → child 挂起不退出，错误配置同样被掩盖）
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 孤儿构造器_真机_child_ready_event缺值或空串按参数错误退出(string? value)
    {
        var exe = ExePath();
        Assert.True(File.Exists(exe), $"构造器未构建：{exe}（gate.ps1 全 sln 构建后应存在）");

        var psi = new ProcessStartInfo(exe) { CreateNoWindow = true, UseShellExecute = false };
        psi.ArgumentList.Add("child");
        psi.ArgumentList.Add("--mem-mb");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("--ready-event");
        if (value is not null)
        {
            psi.ArgumentList.Add(value);
        }
        Process? child = null;
        try
        {
            child = Process.Start(psi);
            Assert.True(child!.WaitForExit(10_000), "child 10s 内未退出（--ready-event 缺值/空串应按参数错误立即退出）");
            Assert.Equal(1, child.ExitCode);
        }
        finally
        {
            // 兜底（同 #44 负例）：守卫若被回归删除，缺值形态落 Sleep(Infinite) 挂起，须杀掉防残留击穿同目录断言
            if (child is not null)
            {
                TryKillById(child.Id);
            }
        }
    }

    // 回归（issue #61 带值参数值为 flag 名）：--out-pid --cpu 原被当作合法值放行 → 静默创建名为
    // "--cpu" 的文件假成功（码 0）；严格校验按「值以 -- 开头视为缺值」参数错误退出码 1，不产生 child。
    // finally 兜底：守卫若被回归删除，parent 将码 0 退出且在 CWD 创建名为 "--cpu" 的脏文件，此处一并回收
    [Fact]
    public void 孤儿构造器_真机_parent带值参数值为flag名按参数错误退出()
    {
        var exe = ExePath();
        Assert.True(File.Exists(exe), $"构造器未构建：{exe}（gate.ps1 全 sln 构建后应存在）");

        var strayFile = Path.Combine(Directory.GetCurrentDirectory(), "--cpu");
        using var parent = Process.Start(new ProcessStartInfo(exe)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            ArgumentList = { "parent", "--mem-mb", "0", "--out-pid", "--cpu" },
        });
        try
        {
            Assert.True(parent!.WaitForExit(15_000), "parent 15s 内未退出（--out-pid 值为 flag 名应立即参数错误退出）");
            Assert.Equal(1, parent.ExitCode);
        }
        finally
        {
            // 兜底（cross-review #61）：守卫若被回归删除，parent 码 0 退出且 child pid 恰写在 "--cpu" 脏文件
            // 首行——文件是唯一回收凭据，先读 pid 击杀挂起 child 再删文件，防残留击穿首测同目录断言
            if (File.Exists(strayFile))
            {
                if (int.TryParse(File.ReadLines(strayFile).FirstOrDefault(), out var leakedPid))
                {
                    TryKillById(leakedPid);
                }
                File.Delete(strayFile);
            }
        }
    }

    // child 侧同症状（issue #61）：--ready-event --window 原取值 "--window" → OpenExisting 崩溃，
    // 退出码落契约外；严格校验按缺值处理参数错误退出码 1
    [Fact]
    public void 孤儿构造器_真机_child带值参数值为flag名按参数错误退出()
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
                ArgumentList = { "child", "--mem-mb", "0", "--ready-event", "--window" },
            });
            Assert.True(child!.WaitForExit(10_000), "child 10s 内未退出（--ready-event 值为 flag 名应立即参数错误退出）");
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

    // 回归（issue #61 未知参数严格拒绝，会话裁决）：拼错参数名 / 裸位置参数原被静默忽略按未提供
    // 处理 → 码 0 假成功；严格校验所有 token 须可归类（已知 flag / 已知带值参数的值），否则码 1。
    // 既有六用例即回归面：负例值经 ArgsAreValid 放行后仍由既有守卫拦为码 1（期望不变），正例形态误拦即红
    [Theory]
    [InlineData("parent", "--out-pd")]   // 拼错带值参数名（--out-pid）
    [InlineData("child", "--mem")]       // 拼错带值参数名（--mem-mb）
    [InlineData("parent", "foo")]        // 裸位置参数（非任何带值参数的值）
    public void 孤儿构造器_真机_未识别参数严格按参数错误退出(string mode, string arg)
    {
        var exe = ExePath();
        Assert.True(File.Exists(exe), $"构造器未构建：{exe}（gate.ps1 全 sln 构建后应存在）");

        // parent 形态追加 --out-pid 临时文件：守卫若被回归删除，派生 child 的 pid 可回收
        //（--out-pd 未识别仍按码 1 断言，覆盖不减损；固定行为世界文件不产生，幂等无副作用）
        var outPidFile = mode == "parent"
            ? Path.Combine(Path.GetTempPath(), $"mrtproc-{Guid.NewGuid():N}.txt")
            : null;
        var psi = new ProcessStartInfo(exe) { CreateNoWindow = true, UseShellExecute = false };
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add(arg);
        if (outPidFile is not null)
        {
            psi.ArgumentList.Add("--out-pid");
            psi.ArgumentList.Add(outPidFile);
        }
        using var proc = Process.Start(psi);
        try
        {
            Assert.True(proc!.WaitForExit(15_000), $"{mode} {arg} 15s 内未退出（未识别参数应立即参数错误退出）");
            Assert.Equal(1, proc.ExitCode);
        }
        finally
        {
            // 兜底（同 #44/#55 负例）：守卫若被回归删除，child 形态落挂起、parent 形态码 0 退出且
            // child pid 已写入 outPidFile——先杀本进程，再按 pid 文件回收派生 child 并删文件，防残留
            // 击穿首测同目录断言
            TryKillById(proc.Id);
            if (outPidFile is not null)
            {
                if (File.Exists(outPidFile)
                    && int.TryParse(File.ReadLines(outPidFile).FirstOrDefault(), out var leakedPid))
                {
                    TryKillById(leakedPid);
                }
                if (File.Exists(outPidFile))
                {
                    File.Delete(outPidFile);
                }
            }
        }
    }

    // 正例锚定（cross-review #61）：参数名大小写不敏感与既有 Has/GetInt 同口径——
    // OrdinalIgnoreCase 若被回归改为 Ordinal，大写形态被误拒码 1、就绪事件永不置位即红
    [Fact]
    public void 孤儿构造器_真机_参数名大写形态放行()
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
                ArgumentList = { "child", "--MEM-MB", "0", "--READY-EVENT", readyName },
            });

            Assert.True(ready.WaitOne(10_000), "child 10s 内未就绪（大写参数名被误拒=校验大小写口径回归）");
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

    // 回归（cross-review #61 实测发现）：重复带值参数校验逃逸——--mem-mb 0 --mem-mb abc 中
    // 第二次出现的非法值逃逸校验（单独传 abc 按码 1 拒绝，垫一个合法值后码 0 放行）。
    // ArgsAreValid 拒绝同名带值参数二次出现（flag 重复幂等无害不动）
    [Fact]
    public void 孤儿构造器_真机_重复带值参数按参数错误退出()
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
                ArgumentList = { "child", "--mem-mb", "0", "--mem-mb", "abc" },
            });
            Assert.True(child!.WaitForExit(10_000), "child 10s 内未退出（重复带值参数应立即参数错误退出）");
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
