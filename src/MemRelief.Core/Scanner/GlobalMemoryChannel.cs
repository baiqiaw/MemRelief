using System.Diagnostics.CodeAnalysis;
using Windows.Win32;
using Windows.Win32.System.SystemInformation;

namespace MemRelief.Core.Scanner;

/// <summary>
/// GlobalMemoryStatusEx 薄通道（概览终底锚点：物理总量恒定锚 + PDH/NtQuery 双败时的 commit 页面文件口径近似，
/// 见 scanner.md §4.1 与 data-contracts.md §2 T-05 裁决）。失败显式抛出（锚点污染上游静默零值，scanner §2 法）。
/// </summary>
/// <remarks>覆盖率豁免（ExcludeFromCodeCoverage）：纯 Win32 互操作薄通道，真机冒烟覆盖；同先例 NativeProcessEnumerator。</remarks>
[ExcludeFromCodeCoverage]
internal static class GlobalMemoryChannel
{
    /// <summary>读全局内存状态快照；GlobalMemoryStatusEx 失败（BOOL=0，结构未定义）→ 显式异常，禁全零静默外泄。</summary>
    internal static GlobalMemorySnapshot QuerySnapshot()
    {
        unsafe
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
            if (!PInvoke.GlobalMemoryStatusEx(&status))
            {
                throw new InvalidOperationException("GlobalMemoryStatusEx 失败（概览终底锚点不可得）");
            }
            return new GlobalMemorySnapshot(
                (long)status.ullTotalPhys, (long)status.ullAvailPhys,
                (long)status.ullTotalPageFile, (long)status.ullAvailPageFile);
        }
    }

    internal readonly record struct GlobalMemorySnapshot(
        long TotalPhysBytes, long AvailPhysBytes, long TotalPageFileBytes, long AvailPageFileBytes);
}
