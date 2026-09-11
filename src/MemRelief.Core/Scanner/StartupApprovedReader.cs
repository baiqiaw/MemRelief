using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32;

namespace MemRelief.Core.Scanner;

/// <summary>
/// StartupApproved 禁用态注册表读取（口径 #12，Run 键/启动文件夹两通道共用，cross-review 收口单点）：
/// 打开指定叶子子键 → 遍历值名 → 禁用态判读（SignalRules.IsDisabledApprovedValue）→ 入
/// OrdinalIgnoreCase 禁用名集。键不存在 = 无禁用记录，空集非失败；读取异常上抛由通道方按单类失败降级。
/// </summary>
/// <remarks>覆盖率豁免（ExcludeFromCodeCoverage）：注册表薄互操作读取，无判定逻辑（判读归纯函数单测承载）；
/// 真机集成冒烟实跑。</remarks>
[ExcludeFromCodeCoverage]
internal static class StartupApprovedReader
{
    private const string SubKeyBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>读单根键下指定叶子（Run/Run32/StartupFolder）的禁用值名集合。</summary>
    public static ISet<string> ReadDisabledNames(RegistryKey root, string leafName)
    {
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var approved = root.OpenSubKey($@"{SubKeyBase}\{leafName}");
        if (approved is null)
        {
            return disabled;
        }
        using (approved)
        foreach (var valueName in approved.GetValueNames())
        {
            if (SignalRules.IsDisabledApprovedValue(approved.GetValue(valueName) as byte[]))
            {
                disabled.Add(valueName);
            }
        }
        return disabled;
    }
}
