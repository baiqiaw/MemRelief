using System.Text;

namespace MemRelief.Core.Scanner;

/// <summary>
/// Run 键命令串 → 可执行路径提取（口径 #12，纯函数，WBS T-03）。
/// Run 键值形态：'"C:\path with spaces\app.exe" -arg'（带引号+参数）、'C:\path\app.exe /flag'（无引号+参数）、
/// 纯路径。环境变量展开由 RegistryKey.GetValue 对 REG_EXPAND_SZ 自动完成（REG_SZ 内嵌 %var% 为已知边界，
/// 保持字面量——精确匹配不命中，数据忠实）。
/// </summary>
public static class RunKeyCommandParser
{
    /// <summary>存在性探测的命令串长度上限：超长（对抗性/损坏数据）直接取首 token，避免探测循环放大 IO 成本。</summary>
    internal const int MaxProbeCommandLength = 4096;

    /// <summary>
    /// 提取规则：带引号取首对引号内内容；未闭合引号剥引号后按无引号规则处理（防参数混入路径）；
    /// 无引号取「存在的文件」最长前缀（Windows 无引号路径二义性的标准解法，前缀 O(n) 增量构建），
    /// 存在性探测仅对本地盘符形态候选执行（UNC/网络路径的 File.Exists 可能秒级阻塞，跳过探测取首 token
    /// 与「均不存在」兜底同语义）。无存在性判定能力（fileExists=null）或均不存在 → 取首个空格前 token（尽力解析）。
    /// 空串/纯空白 → null。
    /// </summary>
    public static string? ExtractExecutablePath(string? rawCommand, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(rawCommand))
        {
            return null;
        }
        var command = rawCommand.Trim();

        if (command[0] == '"')
        {
            var closing = command.IndexOf('"', 1);
            if (closing > 1)
            {
                return command[1..closing];
            }
            // 未闭合/仅一字符引号：剥引号后走无引号逻辑（保底不把参数当路径）
            command = command.Trim('"').Trim();
            if (command.Length == 0)
            {
                return null;
            }
        }

        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        // 前缀 O(n) 增量构建（prefixes[i] = 首 i+1 个 token 拼接），自长向短探测
        var probeEnabled = fileExists is not null && command.Length <= MaxProbeCommandLength;
        var prefixes = new string[tokens.Length];
        var builder = new StringBuilder(command.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }
            builder.Append(tokens[i]);
            prefixes[i] = builder.ToString();
        }

        for (var take = tokens.Length; take >= 2; take--)
        {
            var candidate = prefixes[take - 1];
            if (!probeEnabled || !IsLocalDrivePath(candidate))
            {
                continue;   // 无探测能力/超长/非本地盘符（UNC 等）：不探测，落兜底
            }
            if (fileExists!(candidate))
            {
                return candidate;
            }
        }
        return tokens[0];
    }

    /// <summary>本地盘符形态（"X:\..."）：File.Exists 秒级返回；UNC/设备命名空间路径跳过探测防 SMB 等待。</summary>
    private static bool IsLocalDrivePath(string path) =>
        path.Length >= 3
        && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] is '\\' or '/');
}
