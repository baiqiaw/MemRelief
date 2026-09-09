namespace MemRelief.Core.Scanner;

/// <summary>单进程的服务信号（口径 #8）：进程↔服务名关联 + 失败恢复配置读取结果。</summary>
/// <param name="RestartOnFailure">null=QueryServiceConfig2 读取失败（保守兜底：rules 归🚫服务但不知会否拉起，配 SignalFailure #8）。</param>
public sealed record ServiceSignalInfo(string Name, bool? RestartOnFailure);

/// <summary>
/// 活动信号采集输入（scanner 通道层 → SnapshotAssembler 纯合并层的唯一数据面，T-02）。
/// 五口径通道各自的"全局失败"由 Assemble 统一登记（每口径一条）；per-pid 字典：
/// VisiblePids 仅含存在可见窗口的 pid（缺席=false）；CpuDeltas 恒全量填充（值 null=窗口内退出/不可得，
/// 由合并层配 SignalFailure #7——"缺键"形态生产不可达）。
/// </summary>
public sealed record SignalInputs(
    IReadOnlySet<int> VisiblePids,
    bool WindowEnumerationFailed,
    IReadOnlyDictionary<int, int> TcpEstablished,
    bool TcpTableFailed,
    IReadOnlyDictionary<int, ServiceSignalInfo> Services,
    bool ServiceEnumerationFailed,
    IReadOnlyList<string> SystemDirectoryPrefixes,
    string? UwpPackagePrefix,
    bool DirectoryResolveFailed,
    IReadOnlyDictionary<int, double?> CpuDeltas);
