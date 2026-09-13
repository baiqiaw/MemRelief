using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemRelief.App.Hosting;

/// <summary>
/// 提权重启失败项（PRD F3-6 识别口径：按名称+可执行路径识别上次“需管理员”失败类项）。
/// 仅经启动参数传递（状态不落盘），会话内对象，无持久化。
/// </summary>
public sealed record RestartFailedItem(string Name, string? ExecutablePath);

/// <summary>
/// 重启启动参数（T-27，data-contracts §2 ③.s4 裁决①⑧语义落地）：
/// 自动重扫标志 + 上次失败项清单（名称+可执行路径）。
/// 线格式（T-16 开工裁决，内部一致）：
///   `--memrelief-restart`  重启标志（存在=重启实例，启动后自动扫描；缺省=普通启动不自动扫）
///   `--failed=&lt;base64url(UTF-8 JSON  [{"n":名,"p":路径}]&gt;)`  失败项清单（无失败项时省略；
///     base64url 编码免除命令行引号/空格转义问题）
/// 32K 边界（裁决①）：Windows 命令行总长上限 32767 字符，产生侧 <see cref="Create"/> 超限降级为
/// “不携带失败项、仅自动重扫”；解析侧对超长/损坏载荷防御性同口径丢弃（不信任输入，不抛）。
/// </summary>
public sealed record RestartOptions(bool AutoRescan, IReadOnlyList<RestartFailedItem> FailedItems)
{
    public const string RestartFlag = "--memrelief-restart";
    public const string FailedPrefix = "--failed=";
    public const int CommandLineLimit = 32767;

    private sealed record FailedItemDto(
        [property: JsonPropertyName("n")] string Name,
        [property: JsonPropertyName("p")] string? Path);

    /// <summary>
    /// 产生侧构造：命令行估算总长（可执行路径含引号 + 参数串）超 32K 上限时降级为
    /// 仅自动重扫（失败项清单丢弃——长清单多数项照摆也无从勾选，保自动重扫的可用性）。
    /// </summary>
    public static RestartOptions Create(
        bool autoRescan, IReadOnlyList<RestartFailedItem> failedItems, string? executablePath)
    {
        var candidate = new RestartOptions(autoRescan, failedItems);
        return EstimateCommandLineLength(executablePath, candidate) <= CommandLineLimit
            ? candidate
            : new RestartOptions(autoRescan, []);
    }

    /// <summary>命令行估算总长：`"可执行路径" 参数串`（与 ProcessStartInfo 单参数串拼装一致）。</summary>
    public static int EstimateCommandLineLength(string? executablePath, RestartOptions options)
    {
        var argsLength = string.Join(" ", options.ToArguments()).Length;
        var exeLength = executablePath is null ? 0 : executablePath.Length + 2; // 两侧引号
        return exeLength + (argsLength == 0 ? 0 : 1 + argsLength); // 引号后一个空格
    }

    /// <summary>序列化为启动参数（顺序：标志、失败项；无失败项不携带该参数）。</summary>
    public IReadOnlyList<string> ToArguments()
    {
        if (FailedItems.Count == 0)
        {
            return [RestartFlag];
        }

        var dto = FailedItems.Select(i => new FailedItemDto(i.Name, i.ExecutablePath));
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        return [RestartFlag, FailedPrefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json))];
    }

    /// <summary>
    /// 解析启动参数：仅识别确切前缀（未知参数忽略——普通启动可带任意无关参数，不得误触发自动扫）；
    /// 无重启标志=普通启动（失败项仅重启链路有意义，一并忽略）；损坏/超长载荷防御性降级为仅自动重扫。
    /// </summary>
    public static RestartOptions Parse(IReadOnlyList<string> args)
    {
        var autoRescan = args.Any(a => string.Equals(a, RestartFlag, StringComparison.Ordinal));
        if (!autoRescan)
        {
            return new RestartOptions(false, []);
        }

        var totalLength = args.Sum(a => a.Length) + Math.Max(0, args.Count - 1);
        if (totalLength > CommandLineLimit)
        {
            return new RestartOptions(true, []); // 超长防御：与产生侧降级同口径
        }

        var raw = args.FirstOrDefault(a => a.StartsWith(FailedPrefix, StringComparison.Ordinal));
        if (raw is null)
        {
            return new RestartOptions(true, []);
        }

        var items = DecodeFailedItems(raw[FailedPrefix.Length..]);
        return new RestartOptions(true, items);
    }

    /// <summary>载荷解码：base64url → UTF-8 JSON 数组 → 逐项还原；任一环节失败=空清单（不抛）。</summary>
    private static IReadOnlyList<RestartFailedItem> DecodeFailedItems(string payload)
    {
        try
        {
            if (payload.Length == 0)
            {
                return [];
            }

            var json = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(payload));
            var dtos = JsonSerializer.Deserialize<List<FailedItemDto>>(json, JsonOptions);
            if (dtos is null)
            {
                return [];
            }

            return dtos
                .Where(d => !string.IsNullOrWhiteSpace(d.Name))
                .Select(d => new RestartFailedItem(d.Name, d.Path))
                .ToList();
        }
        catch (Exception)
        {
            // 损坏载荷（base64/JSON/字段形态）：降级为仅自动重扫——重启链路的可用性优先于清单完整性
            return [];
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new();
}
