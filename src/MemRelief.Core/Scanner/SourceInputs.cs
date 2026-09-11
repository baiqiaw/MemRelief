namespace MemRelief.Core.Scanner;

// 来源信号采集数据面（T-03，口径 #12/#15）：通道层探针 → SnapshotAssembler/SourceMatcher 纯合并层。
// 契约落点：SignalSet.SourceEntries / ScheduledTaskWouldRevive（data-contracts §1.1）；口径原文 PRD 口径表 #12/#15。

/// <summary>Run 键自启动条目（口径 #12）：EntryName=注册表值名；ExecutablePath=命令串解析出的可执行路径（环境变量已展开）。</summary>
public sealed record RunKeySource(string EntryName, string ExecutablePath);

/// <summary>
/// 计划任务来源（口径 #12 来源 + #15 拉起）。ExecutablePaths=该任务全部 EXEC 动作的可执行路径；
/// RevivesOnLogonBootOrPeriodic=存在「登录时/启动时/周期」类触发器（PRD 口径 #15 判定前提，与路径匹配在合并层交汇）。
/// </summary>
public sealed record ScheduledTaskSource(string EntryName, IReadOnlyList<string> ExecutablePaths, bool RevivesOnLogonBootOrPeriodic);

/// <summary>启动文件夹条目（口径 #12）：EntryName=快捷方式文件名（含扩展名）；ExecutablePath=.lnk 目标或 .exe 自身。</summary>
public sealed record StartupFolderSource(string EntryName, string ExecutablePath);

/// <summary>
/// 来源三通道采集输入（Run 键[含 StartupApproved 禁用态过滤]/计划任务[ITaskService]/启动文件夹）。
/// 各通道失败旗标=true 时对应清单为空集：空集+旗标≠「已核实无来源」，由 SnapshotAssembler 统一登记
/// SignalFailure #12/#15（契约配对不变量：rules 以 Failures 为保守兜底事实源，data-contracts §1.1）。
/// </summary>
public sealed record SourceInputs(
    IReadOnlyList<RunKeySource> RunKeys,
    bool RunKeyFailed,
    IReadOnlyList<ScheduledTaskSource> ScheduledTasks,
    bool ScheduledTaskFailed,
    IReadOnlyList<StartupFolderSource> StartupFolders,
    bool StartupFolderFailed)
{
    public IReadOnlyList<RunKeySource> RunKeys { get; init; } =
        RunKeys == null ? Array.Empty<RunKeySource>() : RunKeys.ToArray();
    public IReadOnlyList<ScheduledTaskSource> ScheduledTasks { get; init; } =
        ScheduledTasks == null ? Array.Empty<ScheduledTaskSource>() : ScheduledTasks.ToArray();
    public IReadOnlyList<StartupFolderSource> StartupFolders { get; init; } =
        StartupFolders == null ? Array.Empty<StartupFolderSource>() : StartupFolders.ToArray();
}
