using MemRelief.Core.Contracts;

namespace MemRelief.Core.Scanner;

/// <summary>
/// 来源精确匹配（口径 #12/#15 合并侧，纯函数，WBS T-03）：进程可执行路径与来源条目按归一化路径
/// 精确匹配（OrdinalIgnoreCase，不按进程名——PRD 口径 #12 明文），产出 SourceEntries 与
/// ScheduledTaskWouldRevive；服务类来源条目复用 T-02 服务通道数据（条目名=服务名，按 pid 关联，
/// 不依赖路径可得性）。
/// </summary>
public static class SourceMatcher
{
    /// <summary>
    /// 单进程来源匹配。processPath null（路径不可得）→ 仅服务条目 + revive=null（未评估；路径失败记录已由
    /// 编号 100/0 承载，不重复登记）。计划任务拉起（#15）=任务通道成功时，存在「登录/启动/周期」触发器任务
    /// 且其 EXEC 动作路径精确命中本进程。
    /// </summary>
    public static (IReadOnlyList<SourceEntry> Entries, bool? WouldRevive) Match(
        string? processPath, string? serviceName, SourceInputs inputs)
    {
        var entries = new List<SourceEntry>();

        // 服务条目与路径无关（口径 #12"服务：同 #8"，按 pid 关联），路径不可得时不丢
        if (serviceName is not null)
        {
            entries.Add(new SourceEntry(SourceType.Service, serviceName));
        }

        var normalized = SignalRules.NormalizeExecutablePath(processPath);
        if (normalized is null)
        {
            return (entries, null);
        }

        bool PathMatches(string sourcePath) => SignalRules.PathExactEquals(sourcePath, normalized);

        foreach (var runKey in inputs.RunKeys)
        {
            if (PathMatches(runKey.ExecutablePath))
            {
                entries.Add(new SourceEntry(SourceType.RunKey, runKey.EntryName));
            }
        }
        foreach (var task in inputs.ScheduledTasks)
        {
            if (task.ExecutablePaths.Any(PathMatches))
            {
                entries.Add(new SourceEntry(SourceType.ScheduledTask, task.EntryName));
            }
        }
        foreach (var folder in inputs.StartupFolders)
        {
            if (PathMatches(folder.ExecutablePath))
            {
                entries.Add(new SourceEntry(SourceType.StartupFolder, folder.EntryName));
            }
        }

        // 口径 #15：存在「登录/启动/周期」触发器任务且 EXEC 动作路径精确命中本进程 → ⚠️会被拉起。
        // 通道失败时不评估（null=未评估），空集 Any()=false 会伪证"已核实不会被拉起"
        bool? wouldRevive = inputs.ScheduledTaskFailed
            ? null
            : inputs.ScheduledTasks.Any(t =>
                t.RevivesOnLogonBootOrPeriodic && t.ExecutablePaths.Any(PathMatches));

        return (entries, wouldRevive);
    }
}
