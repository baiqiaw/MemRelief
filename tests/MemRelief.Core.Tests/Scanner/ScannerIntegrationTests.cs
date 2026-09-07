using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;

namespace MemRelief.Core.Tests.Scanner;

// Scanner 类名与命名空间段同名，别名消解
using ScannerImpl = MemRelief.Core.Scanner.Scanner;

// 真机集成冒烟（Windows 本机实跑，门禁常驻）：薄通道层无逻辑，以真实系统行为验证通道可用性
// 与字段健全性；采集段耗时经此输出留证（AC：真机 ≤2.0s，正式计时验收载体归 T-21 验证台）。

public class ScannerIntegrationTests
{
    [Fact]
    public void 原生枚举_真机进程数超百且Pid唯一且含自身()
    {
        var rows = new NativeProcessEnumerator().Enumerate().Rows;

        Assert.True(rows.Count > 100, $"真机进程数异常偏少：{rows.Count}");
        Assert.Equal(rows.Count, rows.Select(r => r.Pid).Distinct().Count());
        Assert.Contains(rows, r => r.Pid == Environment.ProcessId);
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Name)));
    }

    [Fact]
    public void 原生枚举_自身进程四字段健全()
    {
        var self = new NativeProcessEnumerator().Enumerate().Rows.Single(r => r.Pid == Environment.ProcessId);

        Assert.Equal(ProcessOpenOutcome.Opened, self.Open);
        Assert.True(self.ExecutablePath.IsOk && self.ExecutablePath.Value!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        Assert.True(self.CreationTimeUtc.IsOk && self.CreationTimeUtc.Value!.Value.Kind == DateTimeKind.Utc, $"创建时间不可读：{self.CreationTimeUtc.Error}");
        Assert.True(self.CreationTimeUtc.Value!.Value > DateTime.UtcNow.AddDays(-30), "自身创建时间不合理");
        Assert.True(self.PrivateCommittedBytes.IsOk && self.PrivateCommittedBytes.Value!.Value > 0);
        // 所有者=当前用户裸用户名（data-contracts §2 T-01 裁决①；术语取"所有者"，PRD 口径#4 "属主"专指窗口归属）
        Assert.True(self.OwnerUser.IsOk, $"所有者采集失败：{self.OwnerUser.Error}");
        Assert.Equal(Environment.UserName, self.OwnerUser.Value, ignoreCase: true);
    }

    [Fact]
    public async Task WMI命令行通道_真机返回含自身命令行()
    {
        var result = await new WmiCommandLineSource().QueryCommandLinesAsync();

        Assert.NotNull(result);
        Assert.True(result!.ContainsKey(Environment.ProcessId));
        var own = result[Environment.ProcessId];
        Assert.NotNull(own); // testhost 为带参启动的托管进程，命令行必非空
        Assert.True(own.Contains("testhost", StringComparison.OrdinalIgnoreCase) || own.Contains("dotnet", StringComparison.OrdinalIgnoreCase),
            $"自身命令行异常：{own}");
    }

    [Fact]
    public async Task TakeSnapshot_真机全链产出契约一致快照且耗时留证()
    {
        var scanner = new ScannerImpl();

        var result = await scanner.TakeSnapshot();

        Assert.True(result.ProcessCount > 100);
        Assert.Equal(result.Snapshots.Count, result.ProcessCount);
        Assert.Equal(DateTimeKind.Utc, result.TakenAtUtc.Kind);
        Assert.Contains(result.Snapshots, s => s.Pid == Environment.ProcessId && s.CommandLine != null);

        // 失败记录框架：真机必有受拒（PPL/系统）进程，Failures 非空且 Pid 可关联
        Assert.NotEmpty(result.Failures);
        Assert.All(result.Failures, f => Assert.False(string.IsNullOrWhiteSpace(f.Detail)));

        // 真实孤儿形态存在性：PPID 指向集外进程或复用者至少其一非零（Windows 常态）
        var orphanish = result.Snapshots.Count(s => s.Signals.OrphanHint != OrphanHint.No);
        Assert.True(orphanish > 0, "真机快照无任何孤儿提示，枚举通道可疑");

        // AC 证据输出：真机 300–600 进程采集段 ≤2.0s
        Console.WriteLine($"[perf-evidence] TakeSnapshot: {result.ProcessCount} procs, {result.Failures.Count} failures, {result.DurationMs}ms (预算 ≤2000ms)");
        Assert.True(result.DurationMs <= 2000, $"采集段超预算：{result.DurationMs}ms");
    }

    [Fact]
    public async Task 未实装方法_显式失败不静默()
    {
        var scanner = new ScannerImpl();
        var snapshot = await scanner.TakeSnapshot();

        // 同步抛出（非 faulted task），块体 lambda 返回 void 使 Throws 捕获同步异常
        Assert.Throws<NotImplementedException>(() => { _ = scanner.CollectSignatures(snapshot, new HashSet<int>()); });
        await Assert.ThrowsAsync<NotImplementedException>(() => scanner.SampleOverview());
    }
}
