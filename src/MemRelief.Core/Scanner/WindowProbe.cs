using System.Diagnostics.CodeAnalysis;

namespace MemRelief.Core.Scanner;

/// <summary>
/// 口径 #4 可见窗口通道（T-02）：EnumWindows 枚举骨架与可见性谓词归 Win32.TopLevelWindowEnumerator
/// 共享层（issue #37 收口），本类只承载候选 pid 的"命中即记"收集语义（"同 pid 多窗口任一可见"由逐窗口
/// 判定等价承载）。可见性谓词语义归 SignalRules（纯函数单测）；"任一可见"聚合缺席 pid=无可见窗口。
/// </summary>
/// <remarks>覆盖率豁免（ExcludeFromCodeCoverage）：P/Invoke 依赖薄层，真机桌面会话冒烟覆盖。</remarks>
[ExcludeFromCodeCoverage]
internal static class WindowProbe
{
    /// <summary>枚举失败 → false（上层按口径 #4 兜底：视为有窗口/不进✅，配全局 SignalFailure）。命中即记，缺席 pid=无可见窗口。</summary>
    public static bool TryCollectVisiblePids(IReadOnlySet<int> candidatePids, out IReadOnlySet<int> visiblePids)
    {
        var results = new HashSet<int>();
        var ok = Win32.TopLevelWindowEnumerator.TryForEachTopLevelWindow((hwnd, pid) =>
        {
            // 首个候选窗口即判定（逐窗口判定、命中即记）
            if (candidatePids.Contains(pid)
                && !results.Contains(pid)
                && Win32.TopLevelWindowEnumerator.IsVisibleCandidate(hwnd))
            {
                results.Add(pid);
            }
        });
        visiblePids = results;
        return ok;
    }
}
