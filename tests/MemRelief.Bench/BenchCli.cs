namespace MemRelief.Bench;

/// <summary>入口辅助（纯函数部分）：命令行参数解析。旗标匹配大小写不敏感（对齐 TestProcs 参数解析先例）。</summary>
public static class BenchCli
{
    /// <summary>
    /// 解析 --top N（谨慎/受保护级明细行数）：缺省 20；非法值/负值回退默认——
    /// 验证台是测量工具，参数错误降级为默认行为而非退出（测量不因展示行数失败）。
    /// </summary>
    public static int ParseTop(IReadOnlyList<string> args)
    {
        const int defaultTop = 20;
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (HasFlag(args, i, "--top") && int.TryParse(args[i + 1], out var parsed) && parsed >= 0)
            {
                return parsed;
            }
        }
        return defaultTop;
    }

    /// <summary>
    /// 解析 --repeat N（同进程连扫轮数）：缺省 1，上限 10。多轮复用同一 Scanner 实例——
    /// 签名验证缓存随实例存活（scanner.md §6），轮 2 起为缓存热态，与 App 常驻复扫形态同构；
    /// 轮 1 为冷缓存首扫。预算对照以缓存热轮为准（system-spec §7 验签预算的现实口径），冷轮如实并报。
    /// </summary>
    public static int ParseRepeat(IReadOnlyList<string> args)
    {
        const int defaultRepeat = 1;
        const int maxRepeat = 10;
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (HasFlag(args, i, "--repeat") && int.TryParse(args[i + 1], out var parsed))
            {
                return Math.Clamp(parsed, 1, maxRepeat);
            }
        }
        return defaultRepeat;
    }

    private static bool HasFlag(IReadOnlyList<string> args, int index, string name) =>
        args[index].Equals(name, StringComparison.OrdinalIgnoreCase);
}
