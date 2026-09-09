using System.Diagnostics.CodeAnalysis;

namespace MemRelief.Core.Scanner;

/// <summary>
/// 口径 #10 系统目录清单解析（T-02）：%windir%（含 System32/SysWOW64/WinSxS 子目录，存在才入清单）+
/// Program Files\WindowsApps（仅用于口径 #4 UWP 识别，不触发"系统目录→按微软处理→🚫"链路，v1.2 修订）。
/// 解析源：环境变量 %windir% 优先，Environment.SystemDirectory / GetFolderPath(ProgramFiles) 兜底（口径"环境变量解析 + Known Folder API"）。
/// 解析失败 → Failed=true（上层按口径 #10 保守兜底：视为系统目录，配全局 SignalFailure）。
/// </summary>
/// <remarks>覆盖率豁免（ExcludeFromCodeCoverage）：环境读取薄层；前缀匹配逻辑在 SignalRules（纯函数单测）。</remarks>
[ExcludeFromCodeCoverage]
internal static class SystemDirectoryResolver
{
    internal static (IReadOnlyList<string> Prefixes, string? UwpPackagePrefix, bool Failed) Resolve()
    {
        try
        {
            var windir = Environment.GetEnvironmentVariable("windir");
            if (string.IsNullOrWhiteSpace(windir))
            {
                // 兜底：System32 反推 %windir%（Known Folder/环境变量口径的备用源）
                var systemDirectory = Environment.SystemDirectory;
                windir = Path.GetDirectoryName(systemDirectory);
            }
            if (string.IsNullOrWhiteSpace(windir) || !Directory.Exists(windir))
            {
                return (Array.Empty<string>(), null, Failed: true);
            }

            var prefixes = new List<string> { windir };
            AddIfExists(prefixes, windir, "System32");
            AddIfExists(prefixes, windir, "SysWOW64");
            AddIfExists(prefixes, windir, "WinSxS");

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var uwp = string.IsNullOrWhiteSpace(programFiles)
                ? null
                : Path.Combine(programFiles, "WindowsApps");

            return (prefixes, Directory.Exists(uwp) ? uwp : null, Failed: false);
        }
        catch (Exception)
        {
            return (Array.Empty<string>(), null, Failed: true);
        }
    }

    private static void AddIfExists(List<string> prefixes, string windir, string subdirectory)
    {
        var full = Path.Combine(windir, subdirectory);
        if (Directory.Exists(full))
        {
            prefixes.Add(full);
        }
    }
}
