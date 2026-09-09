using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace MemRelief.Core.Scanner;

/// <summary>
/// 口径 #4 可见窗口通道（T-02）：EnumWindows 全量顶层窗口一遍 + GetWindowThreadProcessId 归属过滤 +
/// DwmGetWindowAttribute(DWMWA_CLOAKED) 隐藏判定。仅记录"存在可见候选窗口"的 pid（缺席=false）。
/// 可见性谓词归 SignalRules（纯函数单测）；"任一可见"聚合由枚举侧命中即记承载；本类只做 Win32 机械翻译。
/// </summary>
/// <remarks>
/// 覆盖率豁免（ExcludeFromCodeCoverage）：纯 Win32 互操作薄通道；真机桌面会话冒烟覆盖。
/// 手写 P/Invoke 例外（system-spec §4 第④项延伸，T-02 开工裁决）：WNDENUMPROC 回调委托与 DWMWA_CLOAKED
/// 在 allowMarshaling=false 下 CsWin32 生成面不稳（实测缺型），[UnmanagedCallersOnly]+GCHandle 状态承载。
/// </remarks>
[ExcludeFromCodeCoverage]
internal static unsafe partial class WindowProbe
{
    private const int GWLStyle = -16;
    private const int GWLExStyle = -20;
    private const uint DwmwaCloaked = 14;

    [LibraryImport("user32.dll")]
    private static partial int EnumWindows(delegate* unmanaged<nint, nint, int> enumFunc, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, uint* processId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(nint hwnd, int index);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, uint* value, uint size);

    /// <summary>枚举失败 → false（上层按口径 #4 兜底：视为有窗口/不进✅，配全局 SignalFailure）。</summary>
    /// <summary>枚举失败 → false（上层按口径 #4 兜底：视为有窗口/不进✅，配全局 SignalFailure）。命中即记，缺席 pid=无可见窗口。</summary>
    public static bool TryCollectVisiblePids(IReadOnlySet<int> candidatePids, out IReadOnlySet<int> visiblePids)
    {
        var results = new HashSet<int>();
        var state = GCHandle.Alloc(new EnumState(candidatePids, results));
        try
        {
            var ok = EnumWindows(&EnumCallback, (nint)state);
            visiblePids = results;
            return ok != 0;
        }
        finally
        {
            state.Free();
        }
    }

    private sealed class EnumState(IReadOnlySet<int> pids, HashSet<int> results)
    {
        public readonly IReadOnlySet<int> Pids = pids;
        public readonly HashSet<int> Results = results;
    }

    [UnmanagedCallersOnly]
    private static int EnumCallback(nint hwnd, nint lParam)
    {
        // 异常边界：UnmanagedCallersOnly 回调抛托管异常=跨原生帧未定义行为（进程崩溃级），整体吞并恒继续枚举
        //（口径 #4 失败兜底方向不变；静默丢个别窗口判定属保守误降级可接受）
        try
        {
            var state = (EnumState)((GCHandle)lParam).Target!;
            if (TryGetOwningPid(hwnd, out var pid)
                && state.Pids.Contains(pid)
                && !state.Results.Contains(pid))
            {
                // 首个候选窗口即判定（"同 pid 多窗口任一可见"由逐窗口判定、命中即记等价承载）
                var style = GetWindowLong(hwnd, GWLStyle);
                var exStyle = GetWindowLong(hwnd, GWLExStyle);
                if (SignalRules.IsVisibleCandidate(style, exStyle, IsCloaked(hwnd)))
                {
                    state.Results.Add(pid);
                }
            }
        }
        catch
        {
            // 吞并：单窗口判定失败不击穿枚举（Detail 不可达——回调无失败上报通道，量级为个例保守）
        }
        return 1;    // 恒继续枚举
    }

    private static unsafe bool TryGetOwningPid(nint hwnd, out int pid)
    {
        uint owner = 0;
        var threadId = GetWindowThreadProcessId(hwnd, &owner);
        pid = (int)owner;
        return threadId != 0 && pid != 0;
    }

    private static unsafe bool IsCloaked(nint hwnd)
    {
        // DWMWA_CLOAKED=14；非零即被 DWM 隐藏（App/UWP 挂起、InvisibleApp 等）
        var cloaked = 0u;
        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, &cloaked, sizeof(uint)) == 0 && cloaked != 0;
    }
}
