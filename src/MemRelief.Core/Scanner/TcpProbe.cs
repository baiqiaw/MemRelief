using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace MemRelief.Core.Scanner;

/// <summary>
/// 口径 #6 活跃 TCP 通道（T-02）：GetExtendedTcpTable(TCP_TABLE_OWNER_PID_ALL) 全量行 → 按 pid 计 ESTABLISHED。
/// UDP 无连接语义不采集（口径表 #6 明文）；v1 仅 IPv4（AF_INET，IPv6 边界未纳入口径表）。
/// 状态过滤与表行解析归 SignalRules（纯函数构造单测承载）。
/// </summary>
/// <remarks>
/// 覆盖率豁免（ExcludeFromCodeCoverage）：纯互操作薄通道。手写 P/Invoke 例外（system-spec §4 第④项延伸，
/// T-02 开工裁决）：MIB_TCPTABLE_OWNER_PID 为 ANY_SIZE 变长表，allowMarshaling=false 下 CsWin32 生成访问形态不稳。
/// </remarks>
[ExcludeFromCodeCoverage]
internal static partial class TcpProbe
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const uint NoError = 0;

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial uint GetExtendedTcpTable(
        nint tcpTable, ref int tcpTableLength, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, int reserved);

    /// <summary>取表失败 → false（上层按口径 #6 兜底：不进✅，配全局 SignalFailure）。</summary>
    public static bool TryCountEstablishedByPid(IReadOnlySet<int> candidatePids, out IReadOnlyDictionary<int, int> establishedByPid)
    {
        var counts = new Dictionary<int, int>();
        nint buffer = nint.Zero;
        var size = 1 << 16;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                buffer = Marshal.AllocHGlobal(size);
                var status = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
                if (status == NoError)
                {
                    unsafe
                    {
                        SignalRules.CountEstablishedRows((byte*)buffer, size, candidatePids, counts);
                    }
                    establishedByPid = counts;
                    return true;
                }
                Marshal.FreeHGlobal(buffer);
                buffer = nint.Zero;
                // ERROR_INSUFFICIENT_BUFFER(122)：按回填尺寸重试一次（尺寸为 0 时保留原量级防空转）；其余错误直接判败
                if (status != 122 || attempt > 0)
                {
                    establishedByPid = counts;
                    return false;
                }
                size = Math.Max(size + 24, 1 << 16);
            }
        }
        finally
        {
            if (buffer != nint.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
