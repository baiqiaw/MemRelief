using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32;

namespace MemRelief.Core.Scanner;

/// <summary>
/// Run 键通道（口径 #12，WBS T-03）：HKLM\…\Run 64 位 + 32 位双视图（RegistryView 承载 KEY_WOW64 口径，
/// 不硬编码 Wow6432Node 路径）+ HKCU\…\Run 单视图（WOW64 共享键不重定向）；StartupApproved 读禁用态
/// 并滤除禁用条目（禁用条目非有效自启动来源；SourceEntry 契约无启用位）。REG_EXPAND_SZ 由
/// RegistryKey.GetValue 自动展开环境变量；REG_SZ 内嵌 %var% 保持字面量（已知边界，精确匹配不命中）。
/// 禁用名集读取双视图合并（HKLM 的 Run32 叶子不依赖视图重定向布局，64/32 两侧并读消除任务管理器写入侧不确定性）。
/// </summary>
/// <remarks>
/// 覆盖率豁免（ExcludeFromCodeCoverage）：注册表互操作薄通道，无判定逻辑；正常路径真机集成冒烟实跑
/// （同 WmiCommandLineSource 豁免依据）；命令串→路径解析归 RunKeyCommandParser（纯函数，单测承载）；
/// 禁用态读取归 StartupApprovedReader（共用单点）、字节判读归 SignalRules.IsDisabledApprovedValue（纯函数，单测承载）。
/// </remarks>
[ExcludeFromCodeCoverage]
internal static class RunKeySourceProbe
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>注册表读取异常 → false（上层按口径 #12 兜底：来源"采集失败"，配全局 SignalFailure）。
    /// 键不存在不算失败（= 该视图无自启动条目/无禁用记录）。</summary>
    public static bool TryCollect(out IReadOnlyList<RunKeySource> entries)
    {
        List<RunKeySource> collected;
        try
        {
            collected = new List<RunKeySource>();

            // HKLM 64/32 双视图（PRD 口径 #12：64 位 + 32 位两视图都读）
            using (var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            {
                // 禁用态并读：Run/Run32 叶子 × 64/32 两视图取并集（Run32 的视图归属存在写入侧不确定性，
                // 并集方向=宁可多滤；多滤仅损失来源展示，不产生错误关联）
                var hklmDisabled = new HashSet<string>(
                    StartupApprovedReader.ReadDisabledNames(hklm64, "Run")
                        .Concat(StartupApprovedReader.ReadDisabledNames(hklm64, "Run32"))
                        .Concat(StartupApprovedReader.ReadDisabledNames(hklm32, "Run32")),
                    StringComparer.OrdinalIgnoreCase);
                CollectRunKey(hklm64, hklmDisabled, collected);
                CollectRunKey(hklm32, hklmDisabled, collected);
            }

            // HKCU 单视图（PRD 口径 #12：WOW64 共享键不重定向，只读一次）
            CollectRunKey(Registry.CurrentUser, StartupApprovedReader.ReadDisabledNames(Registry.CurrentUser, "Run"), collected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            entries = new List<RunKeySource>();
            return false;
        }

        entries = collected;
        return true;
    }

    /// <summary>
    /// 读单视图 Run 键值条目：字符串值经命令串解析取可执行路径（8.3 短名展开），禁用条目滤除；
    /// 解析不出路径的条目跳过（无路径可精确匹配）。
    /// </summary>
    private static void CollectRunKey(RegistryKey root, ISet<string> disabledNames, List<RunKeySource> collected)
    {
        var runKey = root.OpenSubKey(RunSubKey);
        if (runKey is null)
        {
            return;   // 键不存在 = 该视图无条目，非失败
        }
        using (runKey)
        foreach (var valueName in runKey.GetValueNames())
        {
            if (disabledNames.Contains(valueName))
            {
                continue;   // StartupApproved 禁用态
            }
            if (string.IsNullOrWhiteSpace(valueName))
            {
                continue;   // 默认值/空白名不可展示、不可导航
            }
            var raw = runKey.GetValue(valueName) switch
            {
                string s => s,
                string[] lines => lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)),
                _ => null,   // 非字符串形态（DWORD 等）非命令串，跳过
            };
            if (raw is null)
            {
                continue;
            }
            var path = RunKeyCommandParser.ExtractExecutablePath(raw, File.Exists);
            if (path is not null)
            {
                collected.Add(new RunKeySource(valueName, ShortPathExpander.Expand(path)));
            }
        }
    }
}
