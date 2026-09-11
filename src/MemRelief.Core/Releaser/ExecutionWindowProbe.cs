using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace MemRelief.Core.Releaser;

/// <summary>
/// 执行期窗口自查通道（T-09，data-contracts §2 ③.s4 裁决⑦）：EnumWindows 一遍 + 归属 pid 过滤 +
/// IsVisibleCandidate 谓词（口径 #4 同款：WS_VISIBLE、非 WS_EX_TOOLWINDOW、非 DWM cloaked），
/// 返回目标 pid 全部顶层可见窗口句柄。仅释放决策用（口径 #4 信号仅供判定，不在此复读）。
/// </summary>
/// <remarks>
/// 覆盖率豁免（ExcludeFromCodeCoverage）：纯 Win32 互操作薄通道；真机路径由 T-09 集成测试
/// （TestProcs 窗口形态）实跑验证。手写 P/Invoke 例外同 WindowProbe（T-02 开工裁决①，
/// system-spec §4 第④项）：WNDENUMPROC 回调与 DWMWA_CLOAKED 在 allowMarshaling=false 下
/// CsWin32 生成面不稳，[UnmanagedCallersOnly]+GCHandle 承载状态。
/// </remarks>
[ExcludeFromCodeCoverage]
internal static unsafe partial class ExecutionWindowProbe
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

    /// <summary>
    /// 收集目标 pid 的顶层可见窗口句柄。整体枚举失败 → null（上层按有窗口走优雅路径兜底，裁决⑦）；
    /// 正常无窗口 → 空集（跳过优雅直杀的判据）。
    /// 边界（如实注记）：单窗口判定失败漏收时，"唯一窗口"进程会落空集偏向直杀——该场景多为窗口已销毁，
    /// WM_CLOSE 投递无意义，实害有界（cross-review 收口）。
    /// </summary>
    public static IReadOnlyList<nint>? TryCollectTopLevelWindows(int pid)
    {
        var results = new List<nint>();
        var state = GCHandle.Alloc(new ProbeState(pid, results));
        try
        {
            var ok = EnumWindows(&EnumCallback, (nint)state);
            return ok != 0 ? results : null;
        }
        finally
        {
            state.Free();
        }
    }

    private sealed class ProbeState(int pid, List<nint> results)
    {
        public readonly int Pid = pid;
        public readonly List<nint> Results = results;
    }

    [UnmanagedCallersOnly]
    private static int EnumCallback(nint hwnd, nint lParam)
    {
        // 异常边界：UnmanagedCallersOnly 回调抛托管异常=跨原生帧未定义行为（进程崩溃级），整体吞并恒继续枚举
        //（漏收个别窗口 → 优雅路径兜底方向，保守无害）
        try
        {
            var state = (ProbeState)((GCHandle)lParam).Target!;
            if (TryGetOwningPid(hwnd, out var ownerPid) && ownerPid == state.Pid)
            {
                var style = GetWindowLong(hwnd, GWLStyle);
                var exStyle = GetWindowLong(hwnd, GWLExStyle);
                if (Scanner.SignalRules.IsVisibleCandidate(style, exStyle, IsCloaked(hwnd)))
                {
                    state.Results.Add(hwnd);
                }
            }
        }
        catch
        {
            // 吞并：单窗口判定失败不击穿枚举（漏收单窗口在"唯一窗口"场景偏向直杀，见方法注释边界注记）
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
