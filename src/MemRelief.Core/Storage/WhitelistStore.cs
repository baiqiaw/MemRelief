using System.Text.Json;
using MemRelief.Core.Contracts;

namespace MemRelief.Core.Storage;

// 白名单存储（storage 模块 T-11）。规格见 docs/specs/modules/storage.md §4.1；契约见 data-contracts.md §1.4。
// 法（storage §6）：自有数据文件仅白名单与日志两个（法-4）——本类只写 whitelist.json（.tmp/.corrupt 为其原子写
//   与备份形态，非独立数据文件）；日志文件归 IReleaseLogStore（T-12）。
// 法（storage §6）：白名单文件 schema 变更走 data-contracts.md 变更流程；v1 不设版本字段（契约 §2）。
// 情报（storage §6）：原子写=同目录临时文件 + File.Replace/Move，防中途崩溃损坏。
// 匹配语义：名称一律 OrdinalIgnoreCase；判定侧唯一入口=WhitelistSnapshot.ContainsName（契约 §1.4 唯一匹配点，
//   rules/releaser 一律复用）；存储层键查找（Add 幂等/Remove 定位）与判定同语义，集中于 SameName 一处实现。

/// <summary>损坏自愈记录（ui 启动轻提示"白名单已重置（原文件已备份）"的唯一通道）。</summary>
public sealed record WhitelistRecovery(string BackupPath, string Reason);

/// <summary>白名单存储（storage.md §5 指名接口；消费者=App 编排、ui 白名单管理面板）。</summary>
public interface IWhitelistStore
{
    /// <summary>当前一致视图（不可变；一次扫描取一次，变更源不影响已取快照）。</summary>
    WhitelistSnapshot Snapshot();

    /// <summary>全量条目（含元数据；白名单管理面板数据源）。返回集合与内部状态隔离。</summary>
    IReadOnlyList<WhitelistEntry> List();

    /// <summary>加白（AddedAtUtc 存储层生成 UtcNow）。同名（OrdinalIgnoreCase）幂等：保留首次添加的条目。</summary>
    WhitelistEntry Add(string name, string? path = null, string? note = null);

    /// <summary>按名移除（OrdinalIgnoreCase）。命中=true。移除后重扫即恢复参与判定（PRD F4）。</summary>
    bool Remove(string name);

    /// <summary>构造装载时发生损坏自愈的记录；null=无自愈。</summary>
    WhitelistRecovery? Recovery { get; }
}

public sealed class WhitelistStore : IWhitelistStore
{
    private const string FileName = "whitelist.json";

    // 装载文件大小上限：白名单为 KB 级量级，超限大概率是误写入的非白名单内容——按损坏自愈，
    // 防误粘超大文件拖垮启动内存（评审 D：防御 3 行，走既有自愈语义备份可找回）
    private const long MaxLoadFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,   // 用户可手工编辑，宽容注释与尾逗号
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,                             // 人可读（schema 固定四字段，字段缺省显式 null）
    };

    private readonly object _gate = new();                // 护内存态与盘写：Add/Remove 与 Snapshot/List 并发安全
    private readonly string _directory;
    private readonly string _filePath;
    private List<WhitelistEntry> _entries;

    public WhitelistRecovery? Recovery { get; }

    /// <summary>dataDirectory=null 时锚定用户数据目录（LocalApplicationData，按用户、与提权无关——提权重启后同目录）。</summary>
    public WhitelistStore(string? dataDirectory = null)
    {
        _directory = dataDirectory ?? DefaultDataDirectory();
        _filePath = Path.Combine(_directory, FileName);
        (_entries, Recovery) = Load();
    }

    /// <summary>默认用户数据目录（internal 供测试锚定断言；单点实现见 StorageShared）。</summary>
    internal static string DefaultDataDirectory() => StorageShared.DefaultDataDirectory();

    public WhitelistSnapshot Snapshot()
    {
        lock (_gate) return new WhitelistSnapshot(_entries.ToArray());   // 拷贝即一致视图
    }

    public IReadOnlyList<WhitelistEntry> List()
    {
        lock (_gate) return _entries.ToArray();
    }

    public WhitelistEntry Add(string name, string? path = null, string? note = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        lock (_gate)
        {
            // 幂等：名称是匹配键，重复加白保留首次条目（AddedAtUtc=首次添加时间语义）
            var index = _entries.FindIndex(e => SameName(e, normalized));
            if (index >= 0) return _entries[index];

            var entry = new WhitelistEntry(normalized, DateTime.UtcNow, path, note);
            Persist(new List<WhitelistEntry>(_entries) { entry });   // 先盘后内存：盘失败内存不动
            _entries.Add(entry);
            return entry;
        }
    }

    public bool Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        lock (_gate)
        {
            var index = _entries.FindIndex(e => SameName(e, normalized));
            if (index < 0) return false;

            Persist(_entries.Where((_, i) => i != index).ToList());  // 先盘后内存：盘失败内存不动
            _entries.RemoveAt(index);
            return true;
        }
    }

    /// <summary>存储层键查找谓词（Add 幂等/Remove 定位共用一处，与 ContainsName 同为 OrdinalIgnoreCase）。</summary>
    private static bool SameName(WhitelistEntry entry, string name) =>
        string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase);

    // —— 装载与损坏自愈（R04 GWT：损坏→改名 .corrupt 保留+重建空白；文件缺失=空白名单，非损坏）——

    private (List<WhitelistEntry> Entries, WhitelistRecovery? Recovery) Load()
    {
        // 主文件缺失但有 .corrupt：上次自愈在"备份完成、重建完成前"中断——提示数据在备份中，不静默按空白处理
        if (!File.Exists(_filePath))
        {
            var orphanBackup = _filePath + ".corrupt";
            if (File.Exists(orphanBackup))
                return ([], new WhitelistRecovery(orphanBackup, "白名单文件缺失但存在 .corrupt 备份（上次损坏自愈未完成），数据可从备份人工找回"));
            return ([], null);
        }

        try
        {
            if (new FileInfo(_filePath).Length > MaxLoadFileBytes)
                return Recover("白名单文件异常增大（超过 1MB，疑似误写入非白名单内容）");

            var entries = ParseEntries(File.ReadAllText(_filePath), out var badCount);
            // 非空数组却零有效条目：大概率整个文件内容错乱（非条目级瑕疵）——走损坏自愈备份保留
            if (entries.Count == 0 && badCount > 0)
                return Recover("白名单文件不含任何有效条目（全部元素无法解析）");
            return (entries, null);
        }
        catch (Exception ex)
        {
            return Recover($"白名单文件解析失败：{ex.Message}");
        }
    }

    /// <summary>解析条目数组。结构级错误（非法 JSON/顶层非数组）抛出走自愈；条目级畸形剔除计 badCount。</summary>
    private static List<WhitelistEntry> ParseEntries(string json, out int badCount)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,   // 用户可手工编辑，宽容注释与尾逗号
        });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("顶层应为条目数组");

        badCount = 0;
        var entries = new List<WhitelistEntry>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            // 条目级瑕疵防御性清洗（同 RulePackStore 风格）：非对象元素/null 元素/空白名/字段畸形剔除（名是匹配键，坏条目无意义）
            var entry = element.ValueKind == JsonValueKind.Object
                ? TryParseEntry(element)
                : null;
            if (entry is null || string.IsNullOrWhiteSpace(entry.Name)) { badCount++; continue; }
            entries.Add(entry with { Name = entry.Name.Trim(), AddedAtUtc = StorageShared.NormalizeUtc(entry.AddedAtUtc) });
        }
        return entries;
    }

    private static WhitelistEntry? TryParseEntry(JsonElement element)
    {
        try { return element.Deserialize<WhitelistEntry>(ReadOptions); }
        catch (JsonException) { return null; }   // 单条目字段畸形（如坏时间戳）剔除，不拖垮整份名单
    }

    /// <summary>
    /// 损坏自愈：原文件改名 .corrupt 保留（单槽，覆盖旧备份）、重建空白名单，不阻塞启动。
    /// 备份失败时不重建——原地保留原文件防覆盖未备份内容，下次启动重试自愈（评审 A：数据安全优先）。
    /// </summary>
    private (List<WhitelistEntry>, WhitelistRecovery) Recover(string reason)
    {
        var backupPath = _filePath + ".corrupt";
        try
        {
            File.Move(_filePath, backupPath, overwrite: true);
        }
        catch (Exception ex)
        {
            return ([], new WhitelistRecovery(backupPath, $"{reason}（备份失败：{ex.Message}，原文件原地保留，下次启动重试自愈）"));
        }

        try { Persist([]); }
        catch (Exception ex)
        {
            reason += $"（已备份到 {backupPath}，但重建空白名单失败：{ex.Message}，下次启动重试）";
        }
        return ([], new WhitelistRecovery(backupPath, reason));
    }

    // —— 原子写穿（先盘后内存的唯一写路径；调用方持锁）——

    private void Persist(IReadOnlyList<WhitelistEntry> next)
    {
        Directory.CreateDirectory(_directory);
        var tempPath = _filePath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(next, WriteOptions));
            if (File.Exists(_filePath)) File.Replace(tempPath, _filePath, destinationBackupFileName: null);
            else File.Move(tempPath, _filePath);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* 删除失败不吞原始异常 */ }
            throw;
        }
    }
}
