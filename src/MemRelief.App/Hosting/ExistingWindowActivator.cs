using System.Runtime.InteropServices;

namespace MemRelief.App.Hosting;

/// <summary>
/// 既有窗口激活（T-27 单实例互斥的配套动作，data-contracts §2 ③.s4 裁决⑨“激活既有窗口后退出”）：
/// 按窗口标题（=产品名，MainWindow 构造时取 Core ProductInfo.Name）定位顶层窗口，最小化则还原并前置。
/// Win32 薄通道（system-spec §4 手写例外同族）：无判定逻辑，FindWindow 失败（旧实例已退出）为无操作。
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification =
    "Win32 P/Invoke 薄通道，无判定逻辑可测（与 Core PDH 手写通道同口径）；激活行为依赖真实桌面会话")]
internal static class ExistingWindowActivator
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string title);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwRestore = 9;

    /// <summary>按标题定位并激活既有主窗口；未找到（旧实例已退出/竞态）为无操作。</summary>
    public static void Activate(string windowTitle)
    {
        var hwnd = FindWindow(null, windowTitle);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }
}
