using System.Text;
using System.Text.Json;
using MemRelief.Core.Contracts;

namespace MemRelief.Core.Storage;

// 释放日志存储（storage 模块 T-12）。规格见 docs/specs/modules/storage.md §4.1；契约见 data-contracts.md §1.3/§1.4。
// 落盘形态 = PRD F6 JSONL（data-contracts §1.3：时区/字段映射归本落盘层）：
//   时间=报告 FinishedAtUtc 归一 Utc 后转本地时区 ISO8601（含毫秒）；快照{InUse, commit}=MemoryOverview
//   字节值（单位口径沿契约"一律字节"，MB 仅为展示换算）；逐项结果=outcome 枚举名 + reason（Reason 缺省而
//   ErrorCode 存在时折入"Win32 错误码 N"，失败原因不丢）；主/校验释放量=Main/CheckReleasedBytes 原样。
// 法（storage §6）：自有数据文件仅白名单与日志两个（法-4）——本类只写 releases.jsonl 与其轮转副本（.1~.3，
//   法-4 明示"含轮转副本"），不写其他任何文件。
// 写失败不阻塞释放（PRD §3.7"本次结果未留痕"）：Append 捕获全部异常返回失败结果，绝不抛出；
//   轮转受阻时数据保全优先（同 T-11"备份失败不重建"口径）——本条记录仍写入当前文件并在结果中说明。
// 行级自愈（PRD §3.7"崩溃中途写断"）：追加前检测尾部半行则先补换行（防粘连）；ReadAll 逐行解析，
//   损坏行跳过计数、可解析行保留；损坏内容随轮转移出活动文件（"轮转时清理"）。
// 并发：Append/ReadAll 共用一把锁串行化（编排侧单路径，锁仅防御性；文件共享读允许外部查看器）。

/// <summary>
/// 追加结果（ui 据 Persisted 呈现"本次结果未留痕"）。
/// Persisted=false：Error=失败人读原因；Persisted=true：本条已落盘，Error 非 null 仅为轮转受阻说明（非失败，ui 可轻提示）。
/// </summary>
public sealed record ReleaseLogAppendResult(bool Persisted, string? Error = null);

/// <summary>F6 快照（InUse/commit，字节）。</summary>
public sealed record ReleaseLogSnapshot(long? InUseBytes, long? CommitBytes);

/// <summary>F6 进程清单项（结果=outcome 枚举名；原因含失败原因/Win32 错误码折入）。</summary>
public sealed record ReleaseLogItem(
    int Pid,
    string? Name,
    string? ExecutablePath,
    string? CommandLine,
    string? Outcome,
    string? Reason);

/// <summary>一条可解析释放记录（时间回读为 Utc，与契约时区口径一致）。</summary>
public sealed record ReleaseLogRecord(
    DateTime TimeUtc,
    ReleaseLogSnapshot? Before,
    ReleaseLogSnapshot? After,
    long? MainReleasedBytes,
    long? CheckReleasedBytes,
    IReadOnlyList<ReleaseLogItem> Items);

/// <summary>
/// 跨活动文件与全部轮转副本的读取结果（Records 旧→新；SkippedCorruptLines=损坏行计数；
/// SkippedUnavailableFiles=整体不可读而跳过的文件数——被独占锁/权限拒绝等，其余文件照常读出）。
/// </summary>
public sealed record ReleaseLogReadResult(
    IReadOnlyList<ReleaseLogRecord> Records,
    int SkippedCorruptLines,
    int SkippedUnavailableFiles = 0);

/// <summary>释放日志存储（storage.md §5 指名接口；消费者=App 编排[ReleaseCompleted 后单路径 Append]、ui"打开日志"入口）。</summary>
public interface IReleaseLogStore
{
    /// <summary>活动日志文件全路径（ui"打开日志"入口目标）。</summary>
    string LogFilePath { get; }

    /// <summary>
    /// 追加一条释放记录（JSONL 单行，PRD F6 schema）。写失败返回 Persisted=false 不抛出
    /// （不阻塞释放，PRD §3.7）；report 为 null 亦按失败结果处理（编排侧异常输入不得炸主流程）。
    /// 非幂等：同一报告重复调用即重复落行，调用方须保证一次释放至多一次 Append（编排单路径，
    /// data-contracts §1.5 ReleaseCompleted 至多一次）。
    /// </summary>
    ReleaseLogAppendResult Append(ReleaseReport report);

    /// <summary>
    /// 读取全部可解析记录（活动文件+全部副本，旧→新）。行级自愈：损坏行跳过并计数；
    /// 文件级不可读（独占锁/权限拒绝等 IO 类失败）整体跳过并计入 SkippedUnavailableFiles，
    /// 其余文件照常读出（不因损坏或占用抛出）。
    /// </summary>
    ReleaseLogReadResult ReadAll();
}

public sealed class ReleaseLogStore : IReleaseLogStore
{
    private const string FileStem = "releases";
    private const string FileName = FileStem + ".jsonl";
    private const long MaxRollBytes = 5 * 1024 * 1024;   // 单文件上限 5MB（PRD F6）
    private const int MaxCopies = 3;                     // 保留最近 3 个滚动副本（R06）

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,   // inUse/commit 与 F6 字段名一致
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _filePath;

    /// <summary>活动日志文件全路径。</summary>
    public string LogFilePath => _filePath;

    /// <summary>dataDirectory=null 时锚定用户数据目录（LocalApplicationData，与白名单同目录——提权重启后同目录）。</summary>
    public ReleaseLogStore(string? dataDirectory = null)
    {
        _directory = dataDirectory ?? DefaultDataDirectory();
        _filePath = Path.Combine(_directory, FileName);
    }

    /// <summary>默认用户数据目录（internal 供测试锚定断言，与 WhitelistStore 同目录；单点实现见 StorageShared）。</summary>
    internal static string DefaultDataDirectory() => StorageShared.DefaultDataDirectory();

    public ReleaseLogAppendResult Append(ReleaseReport report)
    {
        if (report is null)
            return new ReleaseLogAppendResult(false, "释放报告为 null，未写入");

        lock (_gate)
        {
            var rotationError = TryRotate();
            try
            {
                Directory.CreateDirectory(_directory);
                var prefix = NeedsNewlineBefore() ? "\n" : "";   // 尾部半行先补换行，防粘连（行级自愈·写侧）
                File.AppendAllText(_filePath, prefix + JsonSerializer.Serialize(ToF6(report), WriteOptions) + "\n",
                    Utf8NoBom);
                return new ReleaseLogAppendResult(true, rotationError);
            }
            catch (Exception ex)
            {
                var error = ex.Message;
                if (rotationError != null) error = $"{rotationError}；追加失败：{error}";
                return new ReleaseLogAppendResult(false, error);
            }
        }
    }

    public ReleaseLogReadResult ReadAll()
    {
        lock (_gate)
        {
            var records = new List<ReleaseLogRecord>();
            var corrupt = 0;
            var unavailable = 0;
            // 旧→新：.3 → .2 → .1 → 活动文件
            for (var i = MaxCopies; i >= 1; i--)
                (corrupt, unavailable) = Accumulate(CopyPath(i), records, corrupt, unavailable);
            (corrupt, unavailable) = Accumulate(_filePath, records, corrupt, unavailable);
            return new ReleaseLogReadResult(records, corrupt, unavailable);
        }
    }

    private (int Corrupt, int Unavailable) Accumulate(string path, List<ReleaseLogRecord> into, int corrupt, int unavailable)
    {
        var (c, u) = ReadFileInto(path, into);
        return (corrupt + c, unavailable + u);
    }

    // —— 滚动（活动文件达 5MB 上限后，下次写入先轮转；保留最近 3 副本，最旧清理）——

    /// <summary>达上限则逐级下移 .2→.3、.1→.2、活动→.1。失败返回人读说明（不抛出，数据保全优先继续追加）。</summary>
    private string? TryRotate()
    {
        try
        {
            if (!File.Exists(_filePath) || new FileInfo(_filePath).Length < MaxRollBytes)
                return null;

            for (var i = MaxCopies; i >= 1; i--)
            {
                var from = i == 1 ? _filePath : CopyPath(i - 1);
                if (!File.Exists(from)) continue;
                File.Move(from, CopyPath(i), overwrite: true);   // 最旧副本被覆盖清理
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"日志滚动受阻（本条记录仍写入当前文件）：{ex.Message}";
        }
    }

    /// <summary>活动文件存在且末字节非换行（崩溃写断的半行）→ 追加前需先补换行。</summary>
    private bool NeedsNewlineBefore()
    {
        if (!File.Exists(_filePath)) return false;
        if (new FileInfo(_filePath).Length == 0) return false;

        using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != '\n';
    }

    private string CopyPath(int n) => Path.Combine(_directory, $"{FileStem}.{n}.jsonl");

    // —— 读侧行级自愈：逐行解析，损坏行跳过计数，可解析行保留 ——
    // 行计损坏的判定：JSON 解析失败，或非 JSON 对象（"null" 字面量/缺 time 键的空壳对象——机器写入
    // 恒有 time，缺 time 无回溯价值；判缺用可空存在性而非零值比较，防零值时刻被误弃）。
    // 文件级自愈：被独占锁/权限拒绝的文件整体跳过计数，其余文件照常读出（不抛出，与 Append 同口径）。

    private (int CorruptLines, int UnavailableFiles) ReadFileInto(string path, List<ReleaseLogRecord> into)
    {
        if (!File.Exists(path)) return (0, 0);

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 1);
        }

        var skipped = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;   // 空行/纯空白不计损坏

            F6Line? f6;
            try { f6 = JsonSerializer.Deserialize<F6Line>(line, ReadOptions); }
            catch (JsonException) { skipped++; continue; }

            if (f6 is null || f6.Time is null) { skipped++; continue; }
            into.Add(ToRecord(f6));
        }
        return (skipped, 0);
    }

    private static ReleaseLogRecord ToRecord(F6Line f6) => new(
        StorageShared.NormalizeUtc(f6.Time!.Value),
        f6.Before is null ? null : new ReleaseLogSnapshot(f6.Before.InUse, f6.Before.Commit),
        f6.After is null ? null : new ReleaseLogSnapshot(f6.After.InUse, f6.After.Commit),
        f6.MainReleasedBytes,
        f6.CheckReleasedBytes,
        (f6.Items ?? []).Select(i => new ReleaseLogItem(
            i.Pid, i.Name, i.ExecutablePath, i.CommandLine, i.Outcome, i.Reason)).ToArray());

    // —— 内存契约 → PRD F6 落盘形态的映射（data-contracts §1.3：映射归本落盘层）——

    private static F6Line ToF6(ReleaseReport report)
    {
        static F6Snapshot? Map(MemoryOverview? overview) => overview is null
            ? null
            : new F6Snapshot(overview.InUseBytes, overview.CommitBytes);

        static string? Reason(ReleaseItemResult item) =>
            string.IsNullOrWhiteSpace(item.Reason) && item.ErrorCode.HasValue
                ? $"Win32 错误码 {item.ErrorCode}"   // 失败原因不丢（R06：逐项结果含失败原因）
                : item.Reason;

        return new F6Line(
            StorageShared.NormalizeUtc(report.FinishedAtUtc).ToLocalTime(),   // F6：时间=本地时区（含毫秒，随序列化格式）
            Map(report.Before),
            Map(report.After),
            report.Items.Select(i => new F6Item(
                i.Pid, i.Name, i.ExecutablePath, i.CommandLine, i.Outcome.ToString(), Reason(i))).ToArray(),
            report.MainReleasedBytes,
            report.CheckReleasedBytes);
    }

    // —— PRD F6 JSONL 行 schema（字段级定义见 PRD F6；camelCase 键与 F6 字段名 inUse/commit 对齐）——

    private sealed record F6Snapshot(long? InUse, long? Commit);

    private sealed record F6Item(
        int Pid, string? Name, string? ExecutablePath, string? CommandLine, string? Outcome, string? Reason);

    private sealed record F6Line(
        DateTime? Time,   // 可空=行缺 time 键（读侧判缺壳行的存在性依据；零值时刻是合法值不判损坏）
        F6Snapshot? Before,
        F6Snapshot? After,
        IReadOnlyList<F6Item>? Items,
        long? MainReleasedBytes,
        long? CheckReleasedBytes);
}
