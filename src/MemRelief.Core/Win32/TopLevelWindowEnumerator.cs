using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace MemRelief.Core.Win32;

/// <summary>
/// EnumWindows 顶层窗口枚举共享通道：P/Invoke 声明、归属 pid 过滤骨架、口径 #4 可见性谓词三值合一
/// （WS_VISIBLE、非 WS_EX_TOOLWINDOW、非 DWM cloaked；语义由 Scanner.SignalRules 纯函数承载）。
/// 消费方各自注入收集语义，禁止第二处 inline 枚举实现（issue #37 收口）：
/// Scanner.WindowProbe（口径 #4 可见 pid 集合）/ Releaser.ExecutionWindowProbe（T-09 执行期窗口自查）。
/// </summary>
/// <remarks>
/// 覆盖率豁免（ExcludeFromCodeCoverage）：纯 Win32 互操作薄通道；真机路径由 TestProcs 窗口形态集成测试
/// 与桌面会话冒烟覆盖。手写 P/Invoke 例外（system-spec §4 第④项，T-02 开工裁决）：WNDENUMPROC 回调与
/// DWMWA_CLOAKED 在 allowMarshaling=false 下 CsWin32 生成面不稳，[UnmanagedCallersOnly]+GCHandle 承载状态。
/// </remarks>
[ExcludeFromCodeCoverage]
internal static unsafe partial class TopLevelWindowEnumerator
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
    /// 枚举全部顶层窗口，逐窗回调 visit(hwnd, ownerPid)。整体枚举失败 → false；
    /// 单窗口判定失败吞并不击穿枚举（漏收的影响方向由消费方兜底语义承载，见 WindowProbe/ExecutionWindowProbe 各自注记）。
    /// </summary>
    public static bool TryForEachTopLevelWindow(Action<nint, int> visit)
    {
        var state = GCHandle.Alloc(visit);
        try
        {
            return EnumWindows(&EnumCallback, (nint)state) != 0;
        }
        finally
        {
            state.Free();
        }
    }

    /// <summary>窗口归属 pid 读取；线程 id 为 0 或 pid 为 0（已销毁竞态）→ false。</summary>
    private static unsafe bool TryGetOwningPid(nint hwnd, out int pid)
    {
        uint owner = 0;
        var threadId = GetWindowThreadProcessId(hwnd, &owner);
        pid = (int)owner;
        return threadId != 0 && pid != 0;
    }

    /// <summary>口径 #4 可见候选窗口谓词（三值合一）：style/exStyle 读数 + DWM cloaked 判定后交纯函数裁决。</summary>
    public static bool IsVisibleCandidate(nint hwnd)
    {
        var style = GetWindowLong(hwnd, GWLStyle);
        var exStyle = GetWindowLong(hwnd, GWLExStyle);
        return Scanner.SignalRules.IsVisibleCandidate(style, exStyle, IsCloaked(hwnd));
    }

    private static unsafe bool IsCloaked(nint hwnd)
    {
        // DWMWA_CLOAKED=14；非零即被 DWM 隐藏（App/UWP 挂起、InvisibleApp 等）
        var cloaked = 0u;
        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, &cloaked, sizeof(uint)) == 0 && cloaked != 0;
    }

    [UnmanagedCallersOnly]
    private static int EnumCallback(nint hwnd, nint lParam)
    {
        // 异常边界：UnmanagedCallersOnly 回调抛托管异常=跨原生帧未定义行为（进程崩溃级），整体吞并恒继续枚举
        try
        {
            var visit = (Action<nint, int>)((GCHandle)lParam).Target!;
            if (TryGetOwningPid(hwnd, out var ownerPid))
            {
                visit(hwnd, ownerPid);
            }
        }
        catch
        {
            // 吞并：单窗口判定失败不击穿枚举（漏收的影响方向由消费方兜底语义承载）
        }
        return 1;    // 恒继续枚举
    }
}
