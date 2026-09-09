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
        // SampleOverview 已于 T-05（issue #9）实装，退出本断言
        Assert.Throws<NotImplementedException>(() => { _ = scanner.CollectSignatures(snapshot, new HashSet<int>()); });
    }
}

// —— T-05 概览采样真机冒烟（通道可用性 + 同刻对照代理；正式资源监视器对照归 T-22）——

public class MemoryOverviewIntegrationTests
{
    [Fact]
    public async Task 概览采样_真机字段健全且降级语义成立()
    {
        var overview = await new ScannerImpl().SampleOverview();

        Assert.True(overview.PhysicalTotalBytes > 0, "物理总量须为正");
        Assert.InRange(overview.InUseBytes, 0, overview.PhysicalTotalBytes);
        Assert.True(overview.CommitLimitBytes > 0 && overview.CommitBytes >= 0, "commit 口径须健全");
        // 偏移错位绊线：Windows commit limit 恒 ≥ 物理内存；错位读值几乎必然击穿其一
        Assert.True(overview.CommitLimitBytes >= overview.PhysicalTotalBytes, "CommitLimit 不得低于物理内存（偏移错位绊线）");
        Assert.True(overview.CommitBytes <= overview.CommitLimitBytes, "Commit 不得超 CommitLimit（偏移错位绊线）");
        if (overview.Source == MemoryOverviewSource.Degraded)
        {
            Assert.Null(overview.StandbyBytes);
        }
        else
        {
            Assert.NotNull(overview.StandbyBytes);
        }
    }

    [Fact]
    public async Task 概览采样_与全局内存状态同刻对照误差在一成内()
    {
        // R05 GWT 自动化代理：与 GlobalMemoryStatusEx 同刻对照（资源监视器正式对照归 T-22 真机验收）
        var overview = await new ScannerImpl().SampleOverview();
        var gms = GlobalMemoryChannel.QuerySnapshot();
        var inUseProxy = gms.TotalPhysBytes - gms.AvailPhysBytes;

        var diff = Math.Abs(overview.InUseBytes - inUseProxy);
        Assert.True(diff <= gms.TotalPhysBytes * 0.10,
            $"同刻对照超 10%：采样 {overview.InUseBytes:N0} vs 全局内存代理 {inUseProxy:N0}（Source={overview.Source}）");
    }

    [Fact]
    public void PDH通道_真机直调_核心计数器健全()
    {
        // 通道梯下健康真机 PDH 分支不可达（NtQuery 恒先成功），直调兜底通道保自动化触发证据
        var ok = PdhMemoryChannel.TryQuery(out var committed, out var limit, out var available, out var standby);

        Assert.True(ok, "PDH 通道真机应可用");
        Assert.True(committed > 0 && limit > 0 && available > 0, $"PDH 计数器异常：committed={committed:N0} limit={limit:N0} avail={available:N0}");
        Assert.True(limit >= committed, "CommitLimit 不得低于 Committed");
        if (standby is not null)
        {
            Assert.True(standby.Value > 0, "standby 非降级时须为正值");
        }
    }
}

// —— T-02 活动信号通道真机冒烟（手写结构偏移/表布局的实证载体；纯判定面在 SignalCollectorTests）——

public class SignalChannelIntegrationTests
{
    [Fact]
    public void 窗口通道_真机枚举成功且存在可见窗口进程()
    {
        var ok = WindowProbe.TryCollectVisiblePids(new HashSet<int>(Enumerable.Range(1, 99999)), out var visiblePids);

        Assert.True(ok, "EnumWindows 真机应成功");
        Assert.NotEmpty(visiblePids);
    }

    [Fact]
    public void tcp通道_真机取表成功且计数非负()
    {
        var ok = TcpProbe.TryCountEstablishedByPid(new HashSet<int>(Enumerable.Range(1, 99999)), out var counts);

        Assert.True(ok, "GetExtendedTcpTable 真机应成功");
        Assert.All(counts.Values, c => Assert.True(c > 0));
    }

    [Fact]
    public void 服务通道_真机关联成立_svchost进程命中关联服务()
    {
        // services.exe 是 SCM 宿主自身，不作为服务进程出现；svchost 分组进程恒有关联服务（真机常态）
        var svchostPid = new NativeProcessEnumerator().Enumerate().Rows
            .Where(r => r.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Pid)
            .ToHashSet();

        Assert.NotEmpty(svchostPid);
        var ok = ServiceProbe.TryCollectServices(out var byPid);

        Assert.True(ok, "SCM 枚举真机应成功");
        Assert.Contains(byPid.Keys, pid => svchostPid.Contains(pid));
        Assert.All(byPid.Keys.Where(svchostPid.Contains),
            pid => Assert.True(byPid[pid].Name.Length > 0, "关联服务名不得为空"));
    }

    [Fact]
    public void cpu差分_真机自身进程差分非负且小于窗口时长()
    {
        var rows = new NativeProcessEnumerator().Enumerate().Rows;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var deltas = new NativeProcessEnumerator().ReadCpuDeltas(rows);
        stopwatch.Stop();

        Assert.True(deltas.TryGetValue(Environment.ProcessId, out var self), "自身进程应采样");
        Assert.NotNull(self);
        Assert.InRange(self!.Value, 0, stopwatch.Elapsed.TotalSeconds + 5);
    }

    [Fact]
    public async Task 端到端_真机快照五信号填充且健康机器无全局采集失败()
    {
        var scan = await new ScannerImpl().TakeSnapshot();

        // 真机健康态：五通道不应有全局级失败（CollectorFailed + Pid=null）
        Assert.DoesNotContain(scan.Failures, f => f.Pid is null && f.Kind == FailureKind.CollectorFailed);

        // 系统目录信号：至少一个进程命中（svchost 路径在 %windir%\System32）
        Assert.Contains(scan.Snapshots, s => s.Signals.IsSystemDirectory == true);

        // 服务信号：svchost 分组进程（真机常态）应关联到具体服务名
        var svchost = scan.Snapshots.FirstOrDefault(s => s.Name == "svchost.exe" && s.Signals.ServiceName is not null);
        Assert.True(svchost is not null, "svchost 应至少一个关联到服务名");

        // 系统目录通道字段可判（解析失败路径之外恒为 bool）；窗口通道已产出判定（bool 语义恒填充）
        Assert.Contains(scan.Snapshots, s => s.Signals.IsSystemDirectory != null);
        Assert.Contains(scan.Snapshots, s => s.Signals.HasVisibleWindow != null);
    }
}

