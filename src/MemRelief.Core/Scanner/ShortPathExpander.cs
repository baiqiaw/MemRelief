using System.Diagnostics.CodeAnalysis;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace MemRelief.Core.Scanner;

/// <summary>
/// 来源侧 8.3 短名展开（PRD 口径表 #3 归一化口径明文「大小写不敏感 + 8.3 短名展开」，cross-review 补遗）：
/// 对 Run 键值/.lnk 目标/任务动作路径等来源路径做长名展开，使与进程侧（QueryFullProcessImageName
/// 恒返回长名形态）精确匹配可比。路径不存在/超长/展开失败 → 原样保留（按原形态参与匹配，不误配）。
/// 纯函数归一化（SignalRules.NormalizeExecutablePath）保持零 I/O，本类是其采集侧 IO 前置步骤。
/// </summary>
/// <remarks>覆盖率豁免（ExcludeFromCodeCoverage）：kernel32 薄互操作通道，无判定逻辑；真机构造短名集成测试
/// 实跑（SourceProbesIntegrationTests）。经 CsWin32 生成（Win32 通道法条），非手写例外。</remarks>
[ExcludeFromCodeCoverage]
internal static class ShortPathExpander
{
    private const uint MaxPathChars = 1024;

    internal static string Expand(string path)
    {
        // GetLongPathName 需要路径真实存在；短名形态仅存在于已落盘路径（注册表/任务里的失效路径展开失败即原样）
        var buffer = new char[MaxPathChars];
        uint length;
        unsafe
        {
            fixed (char* pathPtr = path)
            fixed (char* bufferPtr = buffer)
            {
                length = PInvoke.GetLongPathName(new PCWSTR(pathPtr), new PWSTR(bufferPtr), MaxPathChars);
            }
        }

        // 返回 0=失败（不存在/被拒）；返回值 > 缓冲 = 缓冲不足（按 v1 上限放弃，不二次扩容——来源路径超长本就不可匹配）
        if (length == 0 || length > MaxPathChars)
        {
            return path;
        }
        return new string(buffer, 0, (int)length);
    }
}
