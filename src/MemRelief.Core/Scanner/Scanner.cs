using System.Diagnostics;
using MemRelief.Core.Contracts;

namespace MemRelief.Core.Scanner;

/// <summary>
/// scanner 模块组合实现（IScanner，spec §5）：原生枚举与 WMI 命令行通道重叠执行（编排层设计决策，
/// 缩短采集关键路径），命令行合并为纯函数，装配收口于 SnapshotAssembler（scanner.md §4.1）。
/// 采集段预算 ≤2.0s（system §7 分解）；异步不阻塞调用方。
/// </summary>
public sealed class Scanner : IScanner
{
    private readonly NativeProcessEnumerator _native = new();
    private readonly WmiCommandLineSource _wmi = new();

    /// <summary>采集快照（口径#1/#13 数据侧 + 基础字段 + 失败记录框架）。</summary>
    public async Task<ScanResult> TakeSnapshot()
    {
        var stopwatch = Stopwatch.StartNew();
        // WMI 与原生枚举重叠（WMI 冷启动 COM 初始化较慢）；通道超时/失败→命令行全量 null（裁决⑤）
        var commandLinesTask = _wmi.QueryCommandLinesAsync();

        var (rows, takenAtUtc) = _native.Enumerate();

        var commandLines = await commandLinesTask.ConfigureAwait(false);
        var merged = MergeCommandLines(rows, commandLines);

        stopwatch.Stop();
        return SnapshotAssembler.Assemble(merged, takenAtUtc, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>候选验签（两阶段协议采集侧，口径#9 仅候选执行 + 路径+mtime 缓存）。</summary>
    public Task<ScanResult> CollectSignatures(ScanResult snapshot, ISet<int> candidatePids)
    {
        // T-04 实装（issue #11）；在此之前显式失败，不静默返回未验签数据
        throw new NotImplementedException("CollectSignatures 由 T-04 实装（issue #11）");
    }

    /// <summary>内存概览三数值采样（NtQuerySystemInformation 优先、PDH 三计数器兜底）。</summary>
    public Task<MemoryOverview> SampleOverview()
    {
        // T-05 实装（issue #9）
        throw new NotImplementedException("SampleOverview 由 T-05 实装（issue #9）");
    }

    /// <summary>命令行合并（纯函数）：按 pid 补全；commandLines=null=通道级失败→全量保持 null 无记录（裁决⑤）。</summary>
    /// <remarks>已知边界（v1 接受）：两通道重叠执行存在 pid 复用 TOCTOU 窗口——pid 在枚举后、WMI 查询前退出并被复用时，
    /// 命令行可能错配到新进程。契约无配对键通道（创建时间双键配对归后续版本裁决），T-01 issue 证据评论登记。</remarks>
    internal static List<RawProcess> MergeCommandLines(List<RawProcess> rows, Dictionary<int, string?>? commandLines)
    {
        if (commandLines is null)
        {
            return rows;
        }
        var merged = new List<RawProcess>(rows.Count);
        foreach (var row in rows)
        {
            if (commandLines.TryGetValue(row.Pid, out var commandLine))
            {
                merged.Add(row with { CommandLine = commandLine });
            }
            else
            {
                merged.Add(row);
            }
        }
        return merged;
    }
}
