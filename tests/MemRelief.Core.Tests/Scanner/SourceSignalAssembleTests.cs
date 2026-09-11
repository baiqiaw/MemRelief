using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using MemRelief.Core.Scanner;
using MemRelief.Core.Tests.Rules;

namespace MemRelief.Core.Tests.Scanner;

// T-03 快照装配扩展测试：口径 #3 同目录存活归组、口径 #12/#15 来源匹配装配、
// 来源通道失败旗标 → SignalFailure（配对不变量）、AC②「单类采集失败不进✅级」Classify 级证据。
// 合成数据复用 Rules/TestData 构造器。

public class SourceSignalAssembleTests
{
    private static readonly DateTime BaseTime = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static RawProcess OpenedRow(int pid, int ppid, string path, string name = "app.exe") => new(
        pid, ppid, name, ProcessOpenOutcome.Opened,
        ProcessField<string?>.Ok(path),
        ProcessField<DateTime?>.Ok(BaseTime),
        ProcessField<long?>.Ok(4096),
        ProcessField<string?>.Ok("alice"),
        ProcessField<double?>.Ok(0.1),
        null);

    private static SourceInputs Inputs(
        List<RunKeySource>? runKeys = null,
        List<ScheduledTaskSource>? tasks = null,
        List<StartupFolderSource>? folders = null,
        bool runKeyFailed = false,
        bool taskFailed = false,
        bool folderFailed = false) =>
        SourceTestFactory.Inputs(runKeys, tasks, folders, runKeyFailed, taskFailed, folderFailed);

    // ---------- 口径 #3：同目录存活归组 ----------

    [Fact]
    public void 同目录其他进程_填入SameDirAlivePids且不含自身()
    {
        var rows = new[]
        {
            OpenedRow(10, 0, @"C:\app\app.exe"),
            OpenedRow(11, 0, @"C:\app\helper.exe"),
            OpenedRow(12, 0, @"C:\other\other.exe"),
        };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        var app = result.Snapshots.Single(s => s.Pid == 10);
        Assert.Equal(new HashSet<int> { 11 }, app.Signals.SameDirAlivePids);
        var helper = result.Snapshots.Single(s => s.Pid == 11);
        Assert.Equal(new HashSet<int> { 10 }, helper.Signals.SameDirAlivePids);
        var other = result.Snapshots.Single(s => s.Pid == 12);
        Assert.Empty(other.Signals.SameDirAlivePids);
    }

    [Fact]
    public void 同目录归组_大小写不敏感()
    {
        var rows = new[]
        {
            OpenedRow(10, 0, @"C:\App\App.exe"),
            OpenedRow(11, 0, @"c:\app\helper.exe"),
        };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        Assert.Equal(new HashSet<int> { 11 }, result.Snapshots.Single(s => s.Pid == 10).Signals.SameDirAlivePids);
    }

    [Fact]
    public void 路径不可得行_不参与归组也不获得旁证()
    {
        var denied = new RawProcess(20, 0, "ppl.exe", ProcessOpenOutcome.AccessDenied,
            ProcessField<string?>.Fail("打开被拒"), ProcessField<DateTime?>.Fail("打开被拒"),
            ProcessField<long?>.Fail("打开被拒"), ProcessField<string?>.Fail("打开被拒"),
            ProcessField<double?>.Fail("打开被拒"), null);
        var rows = new RawProcess[]
        {
            denied,
            OpenedRow(10, 0, @"C:\app\app.exe"),
        };

        var result = SnapshotAssembler.Assemble(rows, DateTime.UtcNow, 1);

        Assert.Empty(result.Snapshots.Single(s => s.Pid == 10).Signals.SameDirAlivePids);
    }

    // ---------- 口径 #12/#15：来源装配 ----------

    [Fact]
    public void 来源命中_装配SourceEntries与拉起布尔()
    {
        var rows = new[] { OpenedRow(10, 0, @"C:\tools\launcher.exe") };
        var inputs = SourceTestFactory.SignalInputs(
            sources: Inputs(
                runKeys: new() { new("Launcher", @"C:\tools\launcher.exe") },
                tasks: new() { new(@"\MyTasks\Boot", new List<string> { @"C:\tools\launcher.exe" }, RevivesOnLogonBootOrPeriodic: true) }));

        var result = SnapshotAssembler.Assemble(rows, inputs, DateTime.UtcNow, 1);

        var signals = result.Snapshots.Single().Signals;
        Assert.Equal(2, signals.SourceEntries.Count);
        Assert.Contains(signals.SourceEntries, e => e.Type == SourceType.RunKey && e.EntryName == "Launcher");
        Assert.Contains(signals.SourceEntries, e => e.Type == SourceType.ScheduledTask && e.EntryName == @"\MyTasks\Boot");
        Assert.True(signals.ScheduledTaskWouldRevive);
        Assert.DoesNotContain(result.Failures, f => f.SignalId is 12 or 15);
    }

    [Fact]
    public void 无命中进程_空来源条目且拉起false()
    {
        var rows = new[] { OpenedRow(10, 0, @"C:\plain\app.exe") };
        var inputs = SourceTestFactory.SignalInputs(
            sources: Inputs(runKeys: new() { new("X", @"C:\tools\launcher.exe") }));

        var result = SnapshotAssembler.Assemble(rows, inputs, DateTime.UtcNow, 1);

        var signals = result.Snapshots.Single().Signals;
        Assert.Empty(signals.SourceEntries);
        Assert.False(signals.ScheduledTaskWouldRevive);
    }

    [Fact]
    public void Sources未采集null_字段保持契约默认_T02调用方兼容()
    {
        var rows = new[] { OpenedRow(10, 0, @"C:\app\app.exe") };
        var inputs = SourceTestFactory.SignalInputs();

        var result = SnapshotAssembler.Assemble(rows, inputs, DateTime.UtcNow, 1);

        var signals = result.Snapshots.Single().Signals;
        Assert.Empty(signals.SourceEntries);
        Assert.Null(signals.ScheduledTaskWouldRevive);
        Assert.DoesNotContain(result.Failures, f => f.SignalId is 12 or 15);
    }

    // ---------- 通道失败旗标 → SignalFailure（配对不变量） ----------

    [Fact]
    public void Run键通道失败_登记12号全局CollectorFailed()
    {
        var rows = new[] { OpenedRow(10, 0, @"C:\app\app.exe") };
        var inputs = SourceTestFactory.SignalInputs(
            sources: Inputs(runKeyFailed: true));

        var result = SnapshotAssembler.Assemble(rows, inputs, DateTime.UtcNow, 1);

        var failure = Assert.Single(result.Failures, f => f.SignalId == 12);
        Assert.Null(failure.Pid);
        Assert.Equal(FailureKind.CollectorFailed, failure.Kind);
    }

    [Fact]
    public void 计划任务通道失败_登记12与15双记录()
    {
        // #12（来源采集）与 #15（拉起评估）共用 ITaskService 通道：失败须双口径登记（配对不变量）
        var rows = new[] { OpenedRow(10, 0, @"C:\app\app.exe") };
        var inputs = SourceTestFactory.SignalInputs(
            sources: Inputs(taskFailed: true));

        var result = SnapshotAssembler.Assemble(rows, inputs, DateTime.UtcNow, 1);

        Assert.NotNull(Assert.Single(result.Failures, f => f.SignalId == 12));
        var failure15 = Assert.Single(result.Failures, f => f.SignalId == 15);
        Assert.Null(failure15.Pid);
        Assert.Equal(FailureKind.CollectorFailed, failure15.Kind);
    }

    [Fact]
    public void 启动文件夹通道失败_登记12号全局CollectorFailed()
    {
        var rows = new[] { OpenedRow(10, 0, @"C:\app\app.exe") };
        var inputs = SourceTestFactory.SignalInputs(
            sources: Inputs(folderFailed: true));

        var result = SnapshotAssembler.Assemble(rows, inputs, DateTime.UtcNow, 1);

        var failure = Assert.Single(result.Failures, f => f.SignalId == 12);
        Assert.Null(failure.Pid);
    }

    // ---------- AC②：单类采集失败 → 该进程不进✅级（Classify 级证据） ----------

    [Fact]
    public async Task Run键采集失败_全量进程不进推荐级()
    {
        var snapshot = new ProcessSnapshot(10, 0, "app.exe", @"C:\apps\app.exe",
            BaseTime, 60 * 1024 * 1024,
            Signals: new SignalSet(HasVisibleWindow: false, CpuDeltaSeconds: 0.0, IsSystemDirectory: false));
        var scan = new ScanResult(BaseTime, 1, 500, new[] { snapshot },
            new[] { new SignalFailure(12, null, FailureKind.CollectorFailed, "Run 键/StartupApproved 采集失败") });

        var engine = new RulesEngine();
        var result = await engine.Classify(scan, Snap.Whitelist(), Snap.Pack(), Snap.Ctx());

        var classification = Assert.Single(result);
        Assert.NotEqual(Level.Recommend, classification.Level);
        Assert.Contains(classification.Bases, b => b.SignalId == 0);
    }

    [Fact]
    public async Task 计划任务采集失败_全量进程不进推荐级()
    {
        var snapshot = new ProcessSnapshot(10, 0, "app.exe", @"C:\apps\app.exe",
            BaseTime, 60 * 1024 * 1024,
            Signals: new SignalSet(HasVisibleWindow: false, CpuDeltaSeconds: 0.0, IsSystemDirectory: false));
        var scan = new ScanResult(BaseTime, 1, 500, new[] { snapshot },
            new[] { new SignalFailure(15, null, FailureKind.CollectorFailed, "计划任务拉起信号不可评估") });

        var engine = new RulesEngine();
        var result = await engine.Classify(scan, Snap.Whitelist(), Snap.Pack(), Snap.Ctx());

        Assert.NotEqual(Level.Recommend, Assert.Single(result).Level);
    }
}
