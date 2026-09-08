using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace MemRelief.Core.Scanner;

/// <summary>
/// PDH 内存计数器通道（scanner.md §4.1"PDH 三计数器兜底"）：Committed Bytes/Commit Limit/Available Bytes
/// 三核心计数器 + standby 三计数器（Core/Normal/Reserve，尽力采集，缺任一 → null 降级）。
/// 手写 P/Invoke 例外（system-spec §4 例外口径同类延伸）：PDH_FMT_COUNTERVALUE 为匿名联合，
/// CsWin32 生成面不稳（allowMarshaling=false 下联合访问形态不可控），薄通道自绘更稳；
/// 2026-09-08 T-05 开工裁决，记录于 data-contracts.md §2。
/// </summary>
/// <remarks>覆盖率豁免（ExcludeFromCodeCoverage）：纯互操作薄通道；换算与降级在纯函数 MemoryOverviewSampler。</remarks>
[ExcludeFromCodeCoverage]
internal static partial class PdhMemoryChannel
{
    private const int ErrorSuccess = 0;
    private const uint PdhFmtDouble = 0x00000200;

    private static readonly string[] CoreCounterPaths =
    [
        @"\Memory\Committed Bytes",
        @"\Memory\Commit Limit",
        @"\Memory\Available Bytes",
    ];
    private static readonly string[] StandbyCounterPaths =
    [
        @"\Memory\Standby Cache Core Bytes",
        @"\Memory\Standby Cache Normal Priority Bytes",
        @"\Memory\Standby Cache Reserve Bytes",
    ];

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int PdhOpenQuery(string? dataSource, nuint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int PdhAddEnglishCounter(nint query, string counterPath, nuint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial int PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial int PdhGetFormattedCounterValue(nint counter, uint format, nint counterType, out PdhFmtCounterValue value);

    [LibraryImport("pdh.dll")]
    private static partial int PdhCloseQuery(nint query);

    // PDH_FMT_COUNTERVALUE：DWORD CStatus + 8 字节匿名联合（取 double 视角，PDH_FMT_DOUBLE；x64 对齐 8）
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    /// <summary>采集 commit/available/standby 计数器。任一核心计数器失败 → false（走终底）；standby 缺任一 → null；
    /// standby 合计 ≤0 亦按不可得上报 null（与 NtQuery 全零降级口径对齐）。PDH 不可用 → false。</summary>
    public static bool TryQuery(
        out double committedBytes, out double commitLimitBytes, out double availableBytes, out double? standbyBytes)
    {
        committedBytes = commitLimitBytes = availableBytes = 0;
        standbyBytes = null;
        if (PdhOpenQuery(null, 0, out var query) != ErrorSuccess)
        {
            return false;
        }
        try
        {
            // 先加满计数器、一次 collect，再逐个取格式化值（collect 前取值得到未定义 0 值）
            var coreHandles = new nint[CoreCounterPaths.Length];
            for (var i = 0; i < CoreCounterPaths.Length; i++)
            {
                if (PdhAddEnglishCounter(query, CoreCounterPaths[i], 0, out coreHandles[i]) != ErrorSuccess)
                {
                    return false;
                }
            }
            var standbyHandles = new nint[StandbyCounterPaths.Length];
            var standbyAllAdded = true;
            for (var i = 0; i < StandbyCounterPaths.Length; i++)
            {
                standbyAllAdded &= PdhAddEnglishCounter(query, StandbyCounterPaths[i], 0, out standbyHandles[i]) == ErrorSuccess;
            }
            if (PdhCollectQueryData(query) != ErrorSuccess)
            {
                return false;
            }
            if (!ReadCounter(coreHandles[0], out committedBytes)
                || !ReadCounter(coreHandles[1], out commitLimitBytes)
                || !ReadCounter(coreHandles[2], out availableBytes))
            {
                return false;
            }
            // standby 三计数器尽力采集：任一不可得 → null（上层降级语义承载）
            var standbySum = 0.0;
            var standbyOk = standbyAllAdded;
            for (var i = 0; standbyOk && i < standbyHandles.Length; i++)
            {
                standbyOk = ReadCounter(standbyHandles[i], out var value);
                standbySum += value;
            }
            standbyBytes = standbyOk ? standbySum : null;
            return true;
        }
        finally
        {
            PdhCloseQuery(query);
        }
    }

    private static bool ReadCounter(nint counter, out double value)
    {
        value = 0;
        return PdhGetFormattedCounterValue(counter, PdhFmtDouble, 0, out var formatted) == ErrorSuccess
            && formatted.CStatus == ErrorSuccess
            && (value = formatted.DoubleValue) >= 0;
    }
}
