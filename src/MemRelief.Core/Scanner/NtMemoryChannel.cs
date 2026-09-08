using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace MemRelief.Core.Scanner;

/// <summary>
/// NtQuerySystemInformation 内存通道（手写例外②，system-spec §4）：
/// SystemPerformanceInformation（class 2）取 Available/Committed/CommitLimit 页数，
/// SystemMemoryListInformation（class 80）取 standby 优先级页计数（口径 = 资源监视器 standby 总量）。
/// x64 实证布局（32/64 位同构；布局出处 phnt/ntexapi.h——SDK winternl.h 中该结构为不透明字节阵列不可为据；
/// Geoff Chappell SystemPerformanceInformation 交叉核对；真机同刻对照 + PDH/GlobalMemoryStatusEx 互证验证）——
/// class 2：IdleProcessTime(LARGE_INTEGER)@0、Io 传输计数 3×LARGE_INTEGER@8/16/24、Io 操作计数 3×ULONG@32/36/40、
/// AvailablePages(ULONG)@44、CommittedPages@48、CommitLimit@52；
/// class 80：ZeroPage@0、FreePage@8、ModifiedPage@16、ModifiedNoWrite@24、BadPage@32（各 SIZE_T）、
/// PageCountByPriority[8]@40+i*8。
/// 前提：64 位进程（自用部署 x64，csproj 未锁平台则为部署约定）。守卫按内核返回的实际数据长度（returnLength）。
/// </summary>
/// <remarks>覆盖率豁免（ExcludeFromCodeCoverage）：纯 Win32 互操作薄通道，无判定逻辑；换算与降级在纯函数
/// MemoryOverviewSampler（全量单测）。同先例：NativeProcessEnumerator。</remarks>
[ExcludeFromCodeCoverage]
internal static partial class NtMemoryChannel
{
    private const int SystemPerformanceInformation = 2;
    private const int SystemMemoryListInformation = 80;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusSuccess = 0;

    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(
        int systemInformationClass, byte[] systemInformation, int systemInformationLength, out int returnLength);

    /// <summary>性能信息三页计数；NtQuery 失败（非内存清单原因）→ false 走 PDH 兜底。</summary>
    public static bool TryQueryPerformance(out uint availablePages, out uint committedPages, out uint commitLimitPages)
    {
        availablePages = committedPages = commitLimitPages = 0;
        if (!TryQuery(SystemPerformanceInformation, out var buffer, out var length) || length < 56)
        {
            return false;
        }
        availablePages = ReadUInt32(buffer, 44);
        committedPages = ReadUInt32(buffer, 48);
        commitLimitPages = ReadUInt32(buffer, 52);
        return true;
    }

    /// <summary>standby 优先级页计数（0–7 级原始数组）；清单不可得 → false（上层降级语义）。求和归纯函数。</summary>
    public static bool TryQueryStandbyList(out ulong[] pageCountsByPriority)
    {
        if (!TryQuery(SystemMemoryListInformation, out var buffer, out var length) || length < 104)
        {
            pageCountsByPriority = [];
            return false;
        }
        pageCountsByPriority = Enumerable.Range(0, 8)
            .Select(i => ReadUInt64(buffer, 40 + i * 8))
            .ToArray();
        return true;
    }

    private static bool TryQuery(int infoClass, out byte[] buffer, out int returnLength)
    {
        buffer = new byte[512];
        returnLength = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var status = NtQuerySystemInformation(infoClass, buffer, buffer.Length, out returnLength);
            if (status == StatusSuccess)
            {
                return true;
            }
            if (status != StatusInfoLengthMismatch)
            {
                return false;
            }
            buffer = new byte[Math.Max(returnLength, buffer.Length * 2)];
        }
        return false;
    }

    private static uint ReadUInt32(byte[] buffer, int offset)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));

    private static ulong ReadUInt64(byte[] buffer, int offset)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset));
}
