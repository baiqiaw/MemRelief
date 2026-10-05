namespace MemRelief.Bench;

/// <summary>poolprobe 入口参数解析（纯函数）。旗标匹配大小写不敏感（对齐 BenchCli 先例）。</summary>
public static class PoolProbeCli
{
    /// <summary>缺省规模梯度：覆盖 T-22 实测锚点（10）到 PRD 典型负载上限推演（数百树）。</summary>
    private static readonly int[] DefaultTrees = [10, 50, 100, 200, 300];

    /// <summary>单值/单进程防呆上限（防误输入跑出超长实验）。</summary>
    private const int MaxTrees = 2000;

    /// <summary>
    /// 解析 --trees N[,N...]（承载实验规模列表）：缺省 10,50,100,200,300；
    /// 任一值非法/越界（≤0 或 &gt;2000）或含空段 → 整体回退缺省——
    /// 对齐 BenchCli「参数错误降级为默认」先例，测量工具不因参数退出。
    /// </summary>
    public static IReadOnlyList<int> ParseTrees(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!args[i].Equals("--trees", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = args[i + 1].Split(',');
            var values = new List<int>(parts.Length);
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out var parsed) || parsed <= 0 || parsed > MaxTrees)
                {
                    return DefaultTrees;
                }

                values.Add(parsed);
            }

            return values;
        }

        return DefaultTrees;
    }

    /// <summary>解析 --mode sync|async|both（模式过滤）：缺省 both，非法/缺值回退 both。</summary>
    public static string ParseMode(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!args[i].Equals("--mode", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return args[i + 1].ToLowerInvariant() switch
            {
                "sync" => "sync",
                "async" => "async",
                _ => "both",
            };
        }

        return "both";
    }
}
