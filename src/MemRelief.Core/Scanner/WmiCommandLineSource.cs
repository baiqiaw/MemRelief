using System.Diagnostics.CodeAnalysis;
using System.Management;
using System.Runtime.InteropServices;

namespace MemRelief.Core.Scanner;

/// <summary>
/// 他进程命令行采集（spec 手写例外③：WMI/System.Management 通道，PRD 口径表钉死）。
/// 单批查询全部进程；返回 null=通道级失败/超时（契约 v1：全量字段级 null 表达、无 SignalFailure）。
/// </summary>
/// <remarks>
/// 覆盖率豁免：纯 COM/WMI 互操作适配层，无判定逻辑（机械错误翻译；失败语义=null 降级归编排消费方），
/// 正常路径由真机集成冒烟实跑验证（同 NativeProcessEnumerator 豁免依据，system-spec §7 变更记录 2026-09-07）。
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class WmiCommandLineSource
{
    /// <summary>通道超时兜底防 WMI 服务挂起拖爆采集段预算（data-contracts §2 T-01 裁决⑤）。</summary>
    public static readonly TimeSpan ChannelTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>pid → 命令行；值可能为 null（系统进程无命令行）。超时/通道失败返回 null。</summary>
    public async Task<Dictionary<int, string?>?> QueryCommandLinesAsync()
    {
        try
        {
            var query = Task.Run(QueryAll);
            try
            {
                return await query.WaitAsync(ChannelTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 挂起型失败：通道级失败语义（裁决⑤），查询任务留待 finally 观察防未观察异常
                return null;
            }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                // 超时被弃的查询仍会完成：保底观察其异常，避免 UnobservedTaskException
                _ = query.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            // Task.Run 启动/枚举器创建阶段的同类失败
            return null;
        }
    }

    private static Dictionary<int, string?> QueryAll()
    {
        var result = new Dictionary<int, string?>(512);
        using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process");
        using var results = searcher.Get();
        foreach (var instance in results)
        {
            using var mo = (ManagementObject)instance;
            result[(int)(uint)mo["ProcessId"]!] = mo["CommandLine"] as string;
        }
        return result;
    }
}
