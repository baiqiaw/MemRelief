using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;

namespace MemRelief.Core.Tests.Scanner;

// 来源信号测试共享工厂（T-03）：SourceInputs/SignalInputs 默认构造收敛单点，避免多文件逐字重复。

internal static class SourceTestFactory
{
    public static SourceInputs Inputs(
        List<RunKeySource>? runKeys = null,
        List<ScheduledTaskSource>? tasks = null,
        List<StartupFolderSource>? folders = null,
        bool runKeyFailed = false,
        bool taskFailed = false,
        bool folderFailed = false) => new(
        runKeys ?? new List<RunKeySource>(), runKeyFailed,
        tasks ?? new List<ScheduledTaskSource>(), taskFailed,
        folders ?? new List<StartupFolderSource>(), folderFailed);

    /// <summary>T-02 五通道全默认（无失败、空数据）的 SignalInputs，Sources 可选注入。</summary>
    public static SignalInputs SignalInputs(SourceInputs? sources = null) => new(
        new HashSet<int>(), WindowEnumerationFailed: false,
        new Dictionary<int, int>(), TcpTableFailed: false,
        new Dictionary<int, ServiceSignalInfo>(), ServiceEnumerationFailed: false,
        new List<string>(), UwpPackagePrefix: null, DirectoryResolveFailed: false,
        new Dictionary<int, double?>(),
        Sources: sources);
}
