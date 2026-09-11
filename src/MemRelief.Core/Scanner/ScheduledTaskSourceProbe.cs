using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace MemRelief.Core.Scanner;

/// <summary>
/// 计划任务通道（口径 #12 来源 + #15 拉起，WBS T-03）：ITaskService COM，仅采集根文件夹与用户自定义
/// 文件夹（\Microsoft 子树整体剪枝，\Microsoft\Windows 内建任务不采集——issue AC③）；任务与文件夹均含
/// 隐藏（TASK_ENUM_HIDDEN，隐藏任务同样会拉起进程）。触发器「登录/启动/周期」类（PRD 口径 #15）
/// = TASK_TRIGGER_TIME/DAILY/WEEKLY/MONTHLY/BOOT/LOGON（SDK taskschd.h）。
/// 单任务定义读取失败（ACL 拒读/损坏 XML）计数跳过，存在跳过即整通道失败（部分失败与全成不可区分会
/// 伪证"已核实不拉起"，保守兜底总则方向）；单任务动作路径经环境变量展开与 8.3 短名展开。
/// </summary>
/// <remarks>
/// 技术通道：PRD §3.4 手写例外①「计划任务 ITaskService（COM），单列技术通道，不在 CsWin32 口径内」。
/// 经 ProgID "Schedule.Service" 后期绑定（IDispatch dynamic）——免手写 taskschd 全套接口 GUID/DispId 簿记；
/// 集合遍历走 Count + 默认成员索引器（COM 集合 1 起始）。通道级异常 → false（上层配 SignalFailure #12+#15）。
/// RCW 取舍声明：仅顶层 service 显式 ReleaseComObject，遍历产生的中间 RCW 交由 GC 终结器释放——
/// 本工具非常驻不轮询（PRD §3.4），单次扫描 RCW 量级有限，逐一释放的样板成本大于收益。
/// 覆盖率豁免（ExcludeFromCodeCoverage）：COM 互操作薄通道，无判定逻辑；触发器类型集合与 \Microsoft
/// 剪枝以真机集成冒烟实跑验证（SourceProbesIntegrationTests）。
/// </remarks>
[ExcludeFromCodeCoverage]
internal static class ScheduledTaskSourceProbe
{
    private const string BuiltInFolder = @"\Microsoft";   // 内建任务子树（AC③ 剪枝根）
    private const int TaskActionExec = 0;   // TASK_ACTION_EXEC：启动程序的动作（COM 事务/邮件/弹窗动作不采集）
    private const int TaskEnumHidden = 1;   // TASK_ENUM_HIDDEN（任务与文件夹枚举同义）
    private const int TriggerTime = 1;      // TASK_TRIGGER_TIME（指定时刻，含周期性）
    private const int TriggerDaily = 2;
    private const int TriggerWeekly = 3;
    private const int TriggerMonthly = 4;
    private const int TriggerBoot = 7;      // TASK_TRIGGER_BOOT（启动时）
    private const int TriggerLogon = 8;     // TASK_TRIGGER_LOGON（登录时）

    private static readonly int[] RevivingTriggerTypes =
        { TriggerTime, TriggerDaily, TriggerWeekly, TriggerMonthly, TriggerBoot, TriggerLogon };

    /// <summary>连接/枚举失败 → false（上层按口径 #12/#15 双登记兜底）。</summary>
    public static bool TryCollect(out IReadOnlyList<ScheduledTaskSource> tasks) =>
        TryCollectDetailed(out tasks, out _);

    /// <summary>带访问文件夹清单的采集（folderPaths 供真机测试断言 AC③ 剪枝：无 \Microsoft 前缀）。</summary>
    internal static bool TryCollectDetailed(
        out IReadOnlyList<ScheduledTaskSource> tasks, out IReadOnlyList<string> folderPaths)
    {
        List<ScheduledTaskSource> collected = new();
        List<string> visited = new();
        tasks = collected;
        folderPaths = visited;
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType is null)
            {
                return false;   // 非 Windows / COM 未注册：通道不可用
            }

            var service = Activator.CreateInstance(serviceType);
            if (service is null)
            {
                return false;
            }
            try
            {
                dynamic taskService = service;
                taskService.Connect();
                var skipped = CollectFolder(taskService.GetFolder(@"\"), collected, visited);
                return skipped == 0;   // 存在读取失败的任务即通道失败（保守：部分失败不得伪证"已核实不拉起"）
            }
            finally
            {
                Marshal.ReleaseComObject(service);
            }
        }
        catch (Exception ex) when (ex is COMException
                                   or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                   or InvalidCastException
                                   or UnauthorizedAccessException
                                   or IOException)
        {
            return false;
        }
    }

    /// <summary>递归采集单文件夹任务，返回读取失败的任务数；\Microsoft 子树在进入前剪枝（AC③）。</summary>
    private static int CollectFolder(dynamic folder, List<ScheduledTaskSource> collected, List<string> visited)
    {
        var path = (string)folder.Path;
        if (path.Equals(BuiltInFolder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(BuiltInFolder + @"\", StringComparison.OrdinalIgnoreCase))
        {
            return 0;   // 内建任务不采集（PRD 口径 #12 明文）
        }
        visited.Add(path);

        var skipped = 0;
        var tasks = folder.GetTasks(TaskEnumHidden);
        var taskCount = (int)tasks.Count;
        for (var i = 1; i <= taskCount; i++)
        {
            if (!CollectTask(tasks[i], collected))
            {
                skipped++;
            }
        }

        var folders = folder.GetFolders(TaskEnumHidden);
        var folderCount = (int)folders.Count;
        for (var i = 1; i <= folderCount; i++)
        {
            skipped += CollectFolder(folders[i], collected, visited);
        }
        return skipped;
    }

    /// <summary>单任务：EXEC 动作路径集 + 是否含登录/启动/周期触发器；返回 false=读取失败（计数跳过）。</summary>
    private static bool CollectTask(dynamic registeredTask, List<ScheduledTaskSource> collected)
    {
        try
        {
            var name = (string)registeredTask.Name;
            var definition = registeredTask.Definition;

            var execPaths = new List<string>();
            var actions = definition.Actions;
            var actionCount = (int)actions.Count;
            for (var i = 1; i <= actionCount; i++)
            {
                var action = actions[i];
                if ((int)action.Type == TaskActionExec && !string.IsNullOrWhiteSpace((string?)action.Path))
                {
                    // schtasks/XML 注册的任务动作路径可能含字面环境变量（计划程序执行时才展开）——采集侧对齐展开，
                    // 否则与真实进程路径精确匹配必失配（WouldRevive 伪证 false）；随后做 8.3 短名展开
                    var rawPath = Environment.ExpandEnvironmentVariables(((string)action.Path).Trim());
                    execPaths.Add(ShortPathExpander.Expand(rawPath));
                }
            }
            if (execPaths.Count == 0)
            {
                return true;   // 无 EXEC 动作（COM 事务/邮件/弹窗任务）：既无来源路径也无拉起语义，非失败
            }

            var revives = false;
            var triggers = definition.Triggers;
            var triggerCount = (int)triggers.Count;
            for (var i = 1; i <= triggerCount && !revives; i++)
            {
                var triggerType = (int)triggers[i].Type;
                revives = Array.IndexOf(RevivingTriggerTypes, triggerType) >= 0;
            }

            collected.Add(new ScheduledTaskSource(name, execPaths, revives));
            return true;
        }
        catch (Exception ex) when (ex is COMException
                                   or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                   or InvalidCastException)
        {
            return false;   // 单任务读取失败：计数跳过，收口见 TryCollectDetailed
        }
    }
}
