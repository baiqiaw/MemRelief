using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace MemRelief.Core.Scanner;

/// <summary>
/// 口径 #8 服务关联通道（T-02）：OpenSCManager → EnumServicesStatusExW(SERVICE_WIN32) 按
/// SERVICE_STATUS_PROCESS.dwProcessId 建 pid↔服务关联 → QueryServiceConfig2(SERVICE_CONFIG_FAILURE_ACTIONS)
/// 判"失败恢复动作含 SC_ACTION_RESTART"（⚠️ 会被拉起依据，rules 消费）。
/// 同 pid 多服务归并用 SignalRules.MergeServices（纯函数）；失败恢复动作解析归 SignalRules.HasRestartAction（构造缓冲单测承载）。
/// </summary>
/// <remarks>
/// 覆盖率豁免（ExcludeFromCodeCoverage）：纯互操作薄通道。手写 P/Invoke 例外（system-spec §4 第④项延伸，
/// T-02 开工裁决）：ENUM_SERVICE_STATUS_PROCESS 变长缓冲指针遍历，生成访问形态不稳。
/// x64 布局（SDK um/winsvc.h，实测 2026-09-08 svchost 关联冒烟实证）：条目步长 56（2×指针@0/8 +
/// SERVICE_STATUS_PROCESS 36 字节@16 对齐补 4）；状态块内 dwProcessId@+28。
/// </remarks>
[ExcludeFromCodeCoverage]
internal static partial class ServiceProbe
{
    private const uint ScManagerReadAccess = 0x0005;   // CONNECT|ENUMERATE_SERVICE：只读口径最小权限（普通权限运行，ALL_ACCESS err=5 实证 2026-09-08）
    private const uint ServiceWin32 = 0x0000_0030;
    private const uint ServiceStateAll = 3;
    private const int ScEnumProcessInfo = 0;
    private const uint ServiceQueryConfig = 0x0001;
    private const int ServiceConfigFailureActions = 2;
    private const int StatusBlockOffset = 16;
    private const int ProcessIdOffsetInStatus = 28;
    private const int EntryStride = 56;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;   // EnumServicesStatusEx 缓冲不足回此码（非 122，实测 2026-09-08）

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int EnumServicesStatusExW(
        nint scManager, int infoLevel, uint serviceType, uint serviceState,
        nint lpBuffer, int bufSize, out int bytesNeeded, out int servicesReturned, nint resumeHandle, nint groupName);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint OpenService(nint scManager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int QueryServiceConfig2(
        nint service, int infoLevel, nint buffer, int bufferSize, out int bytesNeeded);

    [LibraryImport("advapi32.dll")]
    private static partial int CloseServiceHandle(nint scObject);

    /// <summary>SCM 打开/枚举失败 → false（上层按口径 #8 兜底：不进✅，配全局 SignalFailure）。</summary>
    public static bool TryCollectServices(out IReadOnlyDictionary<int, ServiceSignalInfo> servicesByPid)
    {
        var merged = new Dictionary<int, List<ServiceSignalInfo>>();
        var scManager = OpenSCManager(null, null, ScManagerReadAccess);
        if (scManager == nint.Zero)
        {
            servicesByPid = new Dictionary<int, ServiceSignalInfo>();
            return false;
        }

        try
        {
            if (!EnumerateAll(scManager, merged))
            {
                servicesByPid = new Dictionary<int, ServiceSignalInfo>();
                return false;
            }
        }
        finally
        {
            CloseServiceHandle(scManager);
        }

        var byPid = new Dictionary<int, ServiceSignalInfo>(merged.Count);
        foreach (var (pid, list) in merged)
        {
            byPid[pid] = SignalRules.MergeServices(list);
        }
        servicesByPid = byPid;
        return true;
    }

    private static unsafe bool EnumerateAll(nint scManager, Dictionary<int, List<ServiceSignalInfo>> merged)
    {
        var size = 1 << 16;
        nint buffer = nint.Zero;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                buffer = Marshal.AllocHGlobal(size);
                var status = EnumServicesStatusExW(
                    scManager, ScEnumProcessInfo, ServiceWin32, ServiceStateAll,
                    buffer, size, out var bytesNeeded, out var returned, nint.Zero, nint.Zero);
                var lastError = Marshal.GetLastWin32Error();   // 释放缓冲前捕获（FreeHGlobal 可能改写 last error）
                if (status != 0)
                {
                    Collect(scManager, buffer, returned, merged);
                    return true;
                }
                Marshal.FreeHGlobal(buffer);
                buffer = nint.Zero;
                // 缓冲不足：EnumServicesStatusEx 回 ERROR_MORE_DATA(234)，非 122；按回填尺寸重试一次，其余错误判败
                if (lastError is not (ErrorInsufficientBuffer or ErrorMoreData) || attempt > 0)
                {
                    return false;
                }
                size = Math.Max(bytesNeeded, size * 2);
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

    private static unsafe void Collect(
        nint scManager, nint buffer, int returned, Dictionary<int, List<ServiceSignalInfo>> merged)
    {
        var basePtr = (byte*)buffer;
        for (var i = 0; i < returned; i++)
        {
            var entry = basePtr + i * EntryStride;
            var serviceNamePtr = *(nint*)entry;
            var pid = *(int*)(entry + StatusBlockOffset + ProcessIdOffsetInStatus);
            if (pid <= 0 || serviceNamePtr == nint.Zero)
            {
                continue;
            }
            var name = new string((char*)serviceNamePtr);
            if (!merged.TryGetValue(pid, out var list))
            {
                list = new List<ServiceSignalInfo>();
                merged[pid] = list;
            }
            list.Add(new ServiceSignalInfo(name, QueryRestartOnFailure(scManager, name)));
        }
    }

    /// <summary>失败恢复配置读取：解析归 SignalRules.HasRestartAction；读取失败 → null（保守，配 SignalFailure #8）。</summary>
    private static unsafe bool? QueryRestartOnFailure(nint scManager, string serviceName)
    {
        var service = OpenService(scManager, serviceName, ServiceQueryConfig);
        if (service == nint.Zero)
        {
            return null;
        }
        try
        {
            var size = 1 << 12;
            nint buffer = nint.Zero;
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    buffer = Marshal.AllocHGlobal(size);
                    var status = QueryServiceConfig2(service, ServiceConfigFailureActions, buffer, size, out var needed);
                    var lastError = Marshal.GetLastWin32Error();   // 释放缓冲前捕获
                    if (status != 0)
                    {
                        bool result;
                        unsafe
                        {
                            result = SignalRules.HasRestartAction((byte*)buffer, size);
                        }
                        return result;
                    }
                    Marshal.FreeHGlobal(buffer);
                    buffer = nint.Zero;
                    if (lastError != ErrorInsufficientBuffer || attempt > 0)
                    {
                        return null;
                    }
                    size = Math.Max(needed, size * 2);
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
        finally
        {
            CloseServiceHandle(service);
        }
    }
}
