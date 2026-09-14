using System.Diagnostics.CodeAnalysis;

namespace MemRelief.Core.Releaser;

/// <summary>
/// 执行期窗口自查通道（T-09，data-contracts §2 ③.s4 裁决⑦）：EnumWindows 枚举骨架与可见性谓词归
/// Win32.TopLevelWindowEnumerator 共享层（issue #37 收口），本类只承载目标 pid 的窗口句柄收集。
/// 仅释放决策用（口径 #4 信号仅供判定，不在此复读）。
/// </summary>
/// <remarks>
/// 覆盖率豁免（ExcludeFromCodeCoverage）：P/Invoke 依赖薄层；真机路径由 T-09 集成测试
/// （TestProcs 窗口形态）实跑验证。
/// </remarks>
[ExcludeFromCodeCoverage]
internal static class ExecutionWindowProbe
{
    /// <summary>
    /// 收集目标 pid 的顶层可见窗口句柄。整体枚举失败 → null（上层按有窗口走优雅路径兜底，裁决⑦）；
    /// 正常无窗口 → 空集（跳过优雅直杀的判据）。
    /// 边界（如实注记）：单窗口判定失败漏收时，"唯一窗口"进程会落空集偏向直杀——该场景多为窗口已销毁，
    /// WM_CLOSE 投递无意义，实害有界（cross-review 收口）。
    /// </summary>
    public static IReadOnlyList<nint>? TryCollectTopLevelWindows(int pid)
    {
        var results = new List<nint>();
        var ok = Win32.TopLevelWindowEnumerator.TryForEachTopLevelWindow((hwnd, ownerPid) =>
        {
            if (ownerPid == pid && Win32.TopLevelWindowEnumerator.IsVisibleCandidate(hwnd))
            {
                results.Add(hwnd);
            }
        });
        return ok ? results : null;
    }
}
