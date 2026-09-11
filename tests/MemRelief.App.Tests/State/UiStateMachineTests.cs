using MemRelief.App.State;

namespace MemRelief.App.Tests.State;

/// <summary>
/// 五态状态机测试（PRD §3.6 状态流转表全矩阵 + 控件可用性矩阵）。
/// AC-1：五态转换与 PRD §3.6 一致（显式枚举+单一转换入口，进行中态禁用矩阵生效）。
/// </summary>
public class UiStateMachineTests
{
    private static UiStateMachine NewMachine() => new();

    // —— 合法转换表：PRD §3.6 逐行（进入条件/离开条件）——
    public static TheoryData<AppState, AppTrigger, AppState> LegalTransitions => new()
    {
        // 未扫描 + 点击“开始扫描”（用户）/管理员重启自动触发（系统）→ 扫描中
        { AppState.NotScanned, AppTrigger.StartScan, AppState.Scanning },
        // 扫描中 + 扫描完成 → 已展示
        { AppState.Scanning, AppTrigger.ScanCompleted, AppState.ResultsShown },
        // 已展示 + 确认弹窗确认 → 释放中（弹窗取消不产生触发，停留本态=无转换）
        { AppState.ResultsShown, AppTrigger.ReleaseConfirmed, AppState.Releasing },
        // 已展示 + 重新扫描 → 扫描中
        { AppState.ResultsShown, AppTrigger.StartScan, AppState.Scanning },
        // 释放中 + 全部树完成/取消 → 结果展示
        { AppState.Releasing, AppTrigger.ReleaseCompleted, AppState.ReportShown },
        // 结果展示 + 关闭报告 → 已展示
        { AppState.ReportShown, AppTrigger.ReportClosed, AppState.ResultsShown },
        // 结果展示 + 重新扫描 → 扫描中
        { AppState.ReportShown, AppTrigger.StartScan, AppState.Scanning },
    };

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    public void 合法转换_到达目标态(AppState initial, AppTrigger trigger, AppState expected)
    {
        var machine = NewMachine().EnteringStateForTest(initial);

        var changed = machine.TryTransition(trigger);

        Assert.True(changed, $"触发 {trigger} 应被接受");
        Assert.Equal(expected, machine.State);
    }

    // —— 全矩阵反向：未在上表的 (态, 触发) 组合一律拒绝且状态不变 ——
    public static TheoryData<AppState, AppTrigger> IllegalTransitions => new()
    {
        // 扫描中/释放中（进行中态）重复触发 → PRD §3.7“重复触发”行：状态机禁用
        { AppState.Scanning, AppTrigger.StartScan },
        { AppState.Scanning, AppTrigger.ReleaseConfirmed },
        { AppState.Scanning, AppTrigger.ReleaseCompleted },
        { AppState.Scanning, AppTrigger.ReportClosed },
        { AppState.Releasing, AppTrigger.StartScan },
        { AppState.Releasing, AppTrigger.ReleaseConfirmed },
        // 未扫描无结果可展示/释放
        { AppState.NotScanned, AppTrigger.ScanCompleted },
        { AppState.NotScanned, AppTrigger.ScanFailed },
        { AppState.NotScanned, AppTrigger.ReleaseConfirmed },
        { AppState.NotScanned, AppTrigger.ReleaseCompleted },
        { AppState.NotScanned, AppTrigger.ReportClosed },
        // 已完成/失败事件不可在非扫描中态消费
        { AppState.ResultsShown, AppTrigger.ScanCompleted },
        { AppState.ResultsShown, AppTrigger.ScanFailed },
        { AppState.ResultsShown, AppTrigger.ReleaseCompleted },
        { AppState.ResultsShown, AppTrigger.ReportClosed },
        { AppState.Releasing, AppTrigger.ScanCompleted },
        { AppState.Releasing, AppTrigger.ScanFailed },
        { AppState.Releasing, AppTrigger.ReportClosed },
        { AppState.ReportShown, AppTrigger.ScanCompleted },
        { AppState.ReportShown, AppTrigger.ScanFailed },
        { AppState.ReportShown, AppTrigger.ReleaseConfirmed },
        { AppState.ReportShown, AppTrigger.ReleaseCompleted },
    };

    [Theory]
    [MemberData(nameof(IllegalTransitions))]
    public void 非法转换_拒绝且状态不变(AppState initial, AppTrigger trigger)
    {
        var machine = NewMachine().EnteringStateForTest(initial);
        var before = machine.State;

        var changed = machine.TryTransition(trigger);

        Assert.False(changed, $"触发 {trigger} 在 {initial} 态应被拒绝");
        Assert.Equal(before, machine.State);
    }

    // —— ScanFailed 分叉（PRD §3.6 扫描中离开条件：首次回未扫描、已有结果回已展示并保留旧结果）——
    [Fact]
    public void 扫描失败_首次无结果_回未扫描()
    {
        var machine = NewMachine();
        machine.TryTransition(AppTrigger.StartScan);

        machine.TryTransition(AppTrigger.ScanFailed);

        Assert.Equal(AppState.NotScanned, machine.State);
        Assert.False(machine.HasResults);
    }

    [Fact]
    public void 扫描失败_已有结果_回已展示并保留结果标记()
    {
        var machine = NewMachine();
        machine.TryTransition(AppTrigger.StartScan);
        machine.TryTransition(AppTrigger.ScanCompleted); // 首次成功 → HasResults=true
        machine.TryTransition(AppTrigger.StartScan);     // 重新扫描

        machine.TryTransition(AppTrigger.ScanFailed);

        Assert.Equal(AppState.ResultsShown, machine.State);
        Assert.True(machine.HasResults);
    }

    [Fact]
    public void 扫描成功_零推荐也算有结果_失败后回已展示()
    {
        // PRD §3.7"扫描零推荐"：正常态渲染非错误——成功即有结果，与失败空态严格区分
        var machine = NewMachine();
        machine.TryTransition(AppTrigger.StartScan);
        machine.TryTransition(AppTrigger.ScanCompleted);

        Assert.True(machine.HasResults);
        machine.TryTransition(AppTrigger.StartScan);
        machine.TryTransition(AppTrigger.ScanFailed);
        Assert.Equal(AppState.ResultsShown, machine.State);
    }

    // —— 控件可用性矩阵（PRD §3.6 前端展示列；进行中态统一禁用）——
    [Theory]
    [InlineData(AppState.NotScanned, true, false, false, false, false)]
    [InlineData(AppState.Scanning, false, false, false, false, false)]
    [InlineData(AppState.ResultsShown, true, true, true, false, false)]
    [InlineData(AppState.Releasing, false, false, false, true, false)]
    [InlineData(AppState.ReportShown, true, false, false, false, true)]
    public void 可用性矩阵_按状态派生(
        AppState state, bool startScan, bool release, bool listInput,
        bool cancelRelease, bool closeReport)
    {
        var machine = NewMachine().EnteringStateForTest(state);

        var a = machine.Availability;

        Assert.Equal(startScan, a.StartScanEnabled);
        Assert.Equal(release, a.ReleaseEnabled);
        Assert.Equal(listInput, a.ListInputEnabled);
        Assert.Equal(cancelRelease, a.CancelReleaseEnabled);
        Assert.Equal(closeReport, a.CloseReportEnabled);
    }

    [Fact]
    public void 状态变化_发出事件_携带新状态()
    {
        var machine = NewMachine();
        var seen = new List<AppState>();
        machine.StateChanged += (_, s) => seen.Add(s);

        machine.TryTransition(AppTrigger.StartScan);

        Assert.Equal([AppState.Scanning], seen);
    }

    [Fact]
    public void 非法转换_不发状态变化事件()
    {
        var machine = NewMachine();
        machine.TryTransition(AppTrigger.StartScan);
        var seen = new List<AppState>();
        machine.StateChanged += (_, s) => seen.Add(s);

        machine.TryTransition(AppTrigger.ReleaseConfirmed); // 扫描中禁用释放

        Assert.Empty(seen);
    }
}

/// <summary>测试辅助：把机器置于指定态。各态一律经真实转换入口铺陈（扫描中态=StartScan 进入，防测试旁路出假态）。</summary>
internal static class UiStateMachineTestExtensions
{
    public static UiStateMachine EnteringStateForTest(this UiStateMachine machine, AppState target)
    {
        switch (target)
        {
            case AppState.NotScanned:
                return machine;
            case AppState.Scanning:
                machine.TryTransition(AppTrigger.StartScan);
                return machine;
            case AppState.ResultsShown:
                machine.TryTransition(AppTrigger.StartScan);
                machine.TryTransition(AppTrigger.ScanCompleted);
                return machine;
            case AppState.Releasing:
                machine.TryTransition(AppTrigger.StartScan);
                machine.TryTransition(AppTrigger.ScanCompleted);
                machine.TryTransition(AppTrigger.ReleaseConfirmed);
                return machine;
            case AppState.ReportShown:
                machine.TryTransition(AppTrigger.StartScan);
                machine.TryTransition(AppTrigger.ScanCompleted);
                machine.TryTransition(AppTrigger.ReleaseConfirmed);
                machine.TryTransition(AppTrigger.ReleaseCompleted);
                return machine;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }
    }
}
