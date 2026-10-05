using System.Diagnostics;
using MemRelief.Core.Contracts;

namespace MemRelief.Core.Scanner;

/// <summary>
/// exe 元数据读取通道（#58）：FileVersionInfo 取 FileDescription/CompanyName，供进程说明展示。
/// 非保护性数据面——单行读取异常仅跳过该行（键缺席=无说明），不产生 SignalFailure、不触发保守兜底
/// （与命令行通道同款"字段级 null"口径，data-contracts §1.1）；读函数构造注入便于测试。
/// </summary>
public sealed class FileDescriptionSource
{
    private readonly Func<string, (string? FileDescription, string? CompanyName)> _read;

    public FileDescriptionSource(Func<string, (string? FileDescription, string? CompanyName)>? reader = null)
    {
        _read = reader ?? ReadFromFile;
    }

    /// <summary>逐行读取（pid → 元组；路径缺失/字段失败 → 键缺席；读取异常 → (null,null) 无说明，
    /// 同路径缓存——展示层与键缺席等价）。永不抛异常；
    /// 同路径多实例按唯一路径读取一次（#58 评审修复：IO 次数=唯一路径数而非进程数）。</summary>
    public Dictionary<int, (string? FileDescription, string? CompanyName)> Read(IReadOnlyList<RawProcess> rows)
    {
        var byPath = new Dictionary<string, (string? FileDescription, string? CompanyName)>();
        var result = new Dictionary<int, (string?, string?)>(rows.Count);
        foreach (var row in rows)
        {
            if (!row.ExecutablePath.IsOk || row.ExecutablePath.Value is not { } path)
            {
                continue;   // 路径缺失/字段失败：无说明（键缺席，非保护性）
            }

            if (!byPath.TryGetValue(path, out var meta))
            {
                try
                {
                    meta = Normalize(_read(path));
                }
                catch (Exception)
                {
                    // 单行读取异常仅跳过（文件消失/属性受限等），不击穿整批
                    meta = ((string?)null, (string?)null);
                }

                byPath[path] = meta;
            }

            result[row.Pid] = meta;
        }

        return result;
    }

    private static (string? FileDescription, string? CompanyName) Normalize(
        (string? FileDescription, string? CompanyName) value) =>
        (NullOrWhiteSpace(value.FileDescription), NullOrWhiteSpace(value.CompanyName));

    private static (string? FileDescription, string? CompanyName) ReadFromFile(string path)
    {
        // 归一化收口 Read 单点（#58 评审修复：注入读函数与生产读函数同一归一化链）
        var info = FileVersionInfo.GetVersionInfo(path);
        return (info.FileDescription, info.CompanyName);
    }

    private static string? NullOrWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
