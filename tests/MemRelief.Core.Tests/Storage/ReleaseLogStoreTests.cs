using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MemRelief.Core.Contracts;
using MemRelief.Core.Storage;
using Xunit;

namespace MemRelief.Core.Tests.Storage;

// 释放日志存储单测（T-12）：JSONL 追加（PRD F6 schema，时区/字段映射归 storage 落盘层）、
// 5MB×3 滚动（R06 GWT）、行级自愈（损坏行跳过可解析）、写失败不阻塞返回结果、并发与时区对抗
//（AC 见 issue #12）
public class ReleaseLogStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "memrelief-t12-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "releases.jsonl");
    private string CopyPath(int n) => Path.Combine(_dir, $"releases.{n}.jsonl");

    private ReleaseLogStore Create() => new(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // —— 构造器：一份可直接断言的释放报告（时间 Kind=Utc，与 ReleaseReport 契约一致）——
    private static ReleaseReport Report(
        DateTime? finishedAtUtc = null,
        IReadOnlyList<ReleaseItemResult>? items = null,
        MemoryOverview? before = null,
        MemoryOverview? after = null,
        long? mainReleasedBytes = null,
        long? checkReleasedBytes = null)
    {
        var t = finishedAtUtc ?? reportFinishedUtc();
        return new ReleaseReport(
            Guid.NewGuid(),
            t.AddMinutes(-1),
            t.AddSeconds(-10),
            t,
            items ?? new List<ReleaseItemResult>
            {
                new(4244, "orphan.exe", @"C:\temp\orphan.exe", "orphan.exe --run",
                    ReleaseItemOutcome.ForceKilled, "优雅关闭超时转强杀"),
                new(99, "dead.exe", null, null, ReleaseItemOutcome.Exited, null),
            },
            before,
            after,
            mainReleasedBytes,
            checkReleasedBytes);
    }

    private static MemoryOverview Overview(long inUse, long commit) =>
        new(16L * 1024 * 1024 * 1024, inUse, commit, 24L * 1024 * 1024 * 1024, null,
            MemoryOverviewSource.NtQuery);

    // 活动文件当前可解析 JSONL 行（只含有效行——含 junk 内容的断言走 ReadAll）
    private List<JsonElement> ReadValidJsonLines(string path) =>
        File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<JsonElement>(l))
            .ToList();

    private static DateTime LineTimeUtc(JsonElement line) =>
        DateTime.Parse(line.GetProperty("time").GetString()!, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    // —— CASE：JSONL 追加按 PRD F6 schema（AC2；R06 GWT：该次 JSONL 记录含全部要素）——

    [Fact]
    public void 释放完成追加_单行JSONL含F6全部字段()
    {
        var store = Create();
        var report = Report(
            before: Overview(10_000_000, 20_000_000),
            after: Overview(8_000_000, 18_000_000),
            mainReleasedBytes: 2_000_000,
            checkReleasedBytes: -1);

        var result = store.Append(report);

        Assert.True(result.Persisted);
        Assert.True(File.Exists(FilePath));
        var line = Assert.Single(ReadValidJsonLines(FilePath));

        Assert.Equal(report.FinishedAtUtc, LineTimeUtc(line));
        Assert.Equal(10_000_000, line.GetProperty("before").GetProperty("inUse").GetInt64());
        Assert.Equal(20_000_000, line.GetProperty("before").GetProperty("commit").GetInt64());
        Assert.Equal(8_000_000, line.GetProperty("after").GetProperty("inUse").GetInt64());
        Assert.Equal(18_000_000, line.GetProperty("after").GetProperty("commit").GetInt64());
        Assert.Equal(2_000_000, line.GetProperty("mainReleasedBytes").GetInt64());
        Assert.Equal(-1, line.GetProperty("checkReleasedBytes").GetInt64());

        var items = line.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(4244, items[0].GetProperty("pid").GetInt32());
        Assert.Equal("orphan.exe", items[0].GetProperty("name").GetString());
        Assert.Equal(@"C:\temp\orphan.exe", items[0].GetProperty("executablePath").GetString());
        Assert.Equal("orphan.exe --run", items[0].GetProperty("commandLine").GetString());
        Assert.Equal("ForceKilled", items[0].GetProperty("outcome").GetString());
        Assert.Equal("优雅关闭超时转强杀", items[0].GetProperty("reason").GetString());
        Assert.Equal("Exited", items[1].GetProperty("outcome").GetString());
    }

    [Fact]
    public void 追加多次_每次一行_追加不覆盖()
    {
        var store = Create();
        Assert.True(store.Append(Report()).Persisted);
        Assert.True(store.Append(Report()).Persisted);

        Assert.Equal(2, ReadValidJsonLines(FilePath).Count);
    }

    // —— CASE：时间字段 = 本地时区 ISO8601 含毫秒（F6 schema；映射归落盘层）——

    [Fact]
    public void 落盘时间为本地时区ISO8601且含毫秒()
    {
        var store = Create();
        store.Append(Report());

        var raw = File.ReadAllText(FilePath).TrimEnd();
        var timeText = JsonSerializer.Deserialize<JsonElement>(raw)
            .GetProperty("time").GetString();

        // ISO8601 含毫秒（小数秒段）且带本地时区偏移
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+", timeText);
        Assert.Matches(@"([+-]\d{2}:\d{2}|Z)$", timeText);
        var parsed = DateTime.Parse(timeText!, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        Assert.Equal(DateTimeKind.Local, parsed.Kind);

        // 与报告 Utc 完成时刻同瞬间（本地钟面 = Utc 换算，任意时区成立）
        Assert.Equal(reportFinishedUtc().ToLocalTime(), parsed);
    }

    private static DateTime reportFinishedUtc() =>
        new(2026, 9, 12, 4, 5, 6, 123, DateTimeKind.Utc);

    // —— CASE：5MB 滚动保留最近 3 副本（AC1；R06 GWT）——

    [Fact]
    public void 活动文件达5MB上限_再次写入_滚动且保留最近3副本旧副本可读()
    {
        var store = Create();
        Assert.True(store.Append(Report()).Persisted);   // 先产生一条有效记录

        // 预置满编副本链：.1/.2/.3 各放可识别内容
        var bigContent = new string('x', 5 * 1024 * 1024);
        File.WriteAllText(FilePath, bigContent);         // 活动文件顶到 5MB
        File.WriteAllText(CopyPath(1), "copy-1");
        File.WriteAllText(CopyPath(2), "copy-2");
        File.WriteAllText(CopyPath(3), "copy-3");

        var result = store.Append(Report());

        Assert.True(result.Persisted);
        Assert.Equal(FilePath, store.LogFilePath);
        // 活动文件换新：仅含本次新记录
        Assert.Single(ReadValidJsonLines(FilePath));
        // 逐级下移：.1=原 5MB 内容（旧副本可读），.2=copy-1，.3=copy-2，原 copy-3 被清理
        Assert.Equal(bigContent, File.ReadAllText(CopyPath(1)));
        Assert.Equal("copy-1", File.ReadAllText(CopyPath(2)));
        Assert.Equal("copy-2", File.ReadAllText(CopyPath(3)));

        // 跨副本读：junk 内容按损坏行跳过，可解析记录保留
        var read = store.ReadAll();
        Assert.Single(read.Records);
        Assert.Equal(3, read.SkippedCorruptLines);   // copy-1/2/3 位置各一行 junk
    }

    [Fact]
    public void 未达5MB_不滚动_追加当前文件()
    {
        var store = Create();
        Assert.True(store.Append(Report()).Persisted);
        var sizeBefore = new FileInfo(FilePath).Length;

        Assert.True(store.Append(Report()).Persisted);

        Assert.False(File.Exists(CopyPath(1)));   // 未滚动
        Assert.True(new FileInfo(FilePath).Length > sizeBefore);
        Assert.Equal(2, ReadValidJsonLines(FilePath).Count);
    }

    [Fact]
    public void 恰好等于5MB_视为达上限_触发滚动()
    {
        Directory.CreateDirectory(_dir);
        var store = Create();
        File.WriteAllText(FilePath, new string('x', 5 * 1024 * 1024));

        Assert.True(store.Append(Report()).Persisted);

        Assert.True(File.Exists(CopyPath(1)));   // 已滚动
        Assert.Single(ReadValidJsonLines(FilePath));
    }

    [Fact]
    public void 差一字节到5MB_不滚动()
    {
        Directory.CreateDirectory(_dir);
        var store = Create();
        File.WriteAllText(FilePath, new string('x', 5 * 1024 * 1024 - 1));

        Assert.True(store.Append(Report()).Persisted);

        Assert.False(File.Exists(CopyPath(1)));   // 未滚动
        var read = store.ReadAll();               // junk 行计损坏，可解析记录恰 1
        Assert.Single(read.Records);
        Assert.Equal(1, read.SkippedCorruptLines);
    }

    // —— CASE：行级自愈——追加写前防半行粘连 + 损坏行跳过可解析（AC2；PRD §3.7）——

    [Fact]
    public void 半行截断_追加前补换行_新记录独立成行可解析()
    {
        var store = Create();
        var good = Report();
        Assert.True(store.Append(good).Persisted);
        // 模拟崩溃写断：砍掉半行且无换行结尾
        var content = File.ReadAllText(FilePath);
        File.WriteAllText(FilePath, content[..(content.Length - 10)]);

        Assert.True(store.Append(Report()).Persisted);

        // 被砍的半行即原记录（已损毁计损坏行）；新记录独立成行可解析（若粘连则整行不可解析，Records=0）
        var read = store.ReadAll();
        Assert.Single(read.Records);
        Assert.Equal(reportFinishedUtc(), read.Records[0].TimeUtc);
        Assert.Equal(1, read.SkippedCorruptLines);
    }

    [Theory]
    [InlineData("""{"time":"2026-09-12T12:00:00.000+08:00","ite""")]   // 截断 JSON
    [InlineData("这不是 JSON")]                                        // 乱码文本
    [InlineData("{\"a\":1}{\"b\":2}")]                                 // 拼接非法
    public void 损坏行_ReadAll跳过并保留可解析行(string corruptLine)
    {
        var store = Create();
        Assert.True(store.Append(Report()).Persisted);
        File.AppendAllText(FilePath, corruptLine + "\n");
        Assert.True(store.Append(Report(finishedAtUtc:
            new DateTime(2026, 9, 12, 8, 0, 0, DateTimeKind.Utc))).Persisted);

        var read = store.ReadAll();

        Assert.Equal(2, read.Records.Count);
        Assert.Equal(1, read.SkippedCorruptLines);
        Assert.Equal(new DateTime(2026, 9, 12, 8, 0, 0, DateTimeKind.Utc), read.Records[1].TimeUtc);
    }

    [Fact]
    public void 空行与纯空白行_不计损坏行()
    {
        var store = Create();
        Assert.True(store.Append(Report()).Persisted);
        File.AppendAllText(FilePath, "\n   \n");

        var read = store.ReadAll();

        Assert.Single(read.Records);
        Assert.Equal(0, read.SkippedCorruptLines);
    }

    // —— CASE：写失败不阻塞释放，返回失败结果（AC3；PRD §3.7"本次结果未留痕"）——

    [Fact]
    public void 活动文件只读_返回失败结果且不抛异常()
    {
        var store = Create();
        Assert.True(store.Append(Report()).Persisted);
        File.SetAttributes(FilePath, FileAttributes.ReadOnly);
        try
        {
            var result = store.Append(Report());

            Assert.False(result.Persisted);   // ui 呈现"本次结果未留痕"依据
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }
        finally
        {
            File.SetAttributes(FilePath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void 活动文件被独占占用_返回失败结果且不抛异常()
    {
        var store = Create();
        Assert.True(store.Append(Report()).Persisted);

        using (File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = store.Append(Report());

            Assert.False(result.Persisted);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }
    }

    [Fact]
    public void 数据目录不可创建_返回失败结果且不抛异常()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "占用目录位的文件");
        var store = new ReleaseLogStore(Path.Combine(blocker, "sub"));   // 以文件为父的路径必失败

        var result = store.Append(Report());

        Assert.False(result.Persisted);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // —— CASE：并发 Append（对抗：串行化后全部落盘且逐行可解析）——

    [Fact]
    public void 并发Append_全部落盘且逐行可解析()
    {
        var store = Create();
        const int threads = 8, perThread = 3;

        var results = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            var list = new List<ReleaseLogAppendResult>();
            for (var i = 0; i < perThread; i++)
                list.Add(store.Append(Report(finishedAtUtc:
                    new DateTime(2026, 9, 12, 8, 0, t * perThread + i, DateTimeKind.Utc))));
            return list;
        })).SelectMany(r => r.Result).ToList();

        Assert.All(results, r => Assert.True(r.Persisted));
        var read = store.ReadAll();
        Assert.Equal(threads * perThread, read.Records.Count);
        Assert.Equal(0, read.SkippedCorruptLines);
    }

    // —— CASE：时区归一（对抗：非 Utc Kind 输入归一后映射本地落盘，读取回 Utc；任意时区成立）——

    [Fact]
    public void 输入时间Kind非Utc_归一语义与白名单同款_读取回Utc一致()
    {
        var store = Create();
        var unspecified = new DateTime(2026, 9, 12, 4, 5, 6, 123, DateTimeKind.Unspecified);
        var local = new DateTime(2026, 9, 12, 12, 5, 6, 123, DateTimeKind.Local);
        // 期望按 WhitelistStore.NormalizeUtc 同款语义：Unspecified 视为 Utc 钟面；Local 真转 Utc
        var expectedUnspecified = DateTime.SpecifyKind(unspecified, DateTimeKind.Utc);
        var expectedLocal = local.ToUniversalTime();

        Assert.True(store.Append(Report(finishedAtUtc: unspecified)).Persisted);
        Assert.True(store.Append(Report(finishedAtUtc: local)).Persisted);

        var read = store.ReadAll();
        Assert.Equal(2, read.Records.Count);
        Assert.Equal(expectedUnspecified, read.Records[0].TimeUtc);
        Assert.Equal(expectedLocal, read.Records[1].TimeUtc);
    }

    // —— CASE：空值对抗（Before/After 采样失败为 null、Reason 缺省、ErrorCode 折入原因、空清单）——

    [Fact]
    public void 快照为null与缺省原因_落盘null且ErrorCode折入原因()
    {
        var store = Create();
        var report = Report(
            items: new List<ReleaseItemResult>
            {
                new(7, "blocked.exe", @"C:\x\blocked.exe", null, ReleaseItemOutcome.Blocked, null, ErrorCode: 5),
                new(8, "ok.exe", null, null, ReleaseItemOutcome.Released, "已优雅关闭"),
            },
            before: null,
            after: null,
            mainReleasedBytes: null,
            checkReleasedBytes: null);

        Assert.True(store.Append(report).Persisted);

        var line = ReadValidJsonLines(FilePath).Single();
        Assert.Equal(JsonValueKind.Null, line.GetProperty("before").ValueKind);
        Assert.Equal(JsonValueKind.Null, line.GetProperty("after").ValueKind);
        Assert.Equal(JsonValueKind.Null, line.GetProperty("mainReleasedBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, line.GetProperty("checkReleasedBytes").ValueKind);

        var items = line.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("Win32 错误码 5", items[0].GetProperty("reason").GetString());   // 失败原因不丢
        Assert.Equal("已优雅关闭", items[1].GetProperty("reason").GetString());

        var record = Assert.Single(store.ReadAll().Records);
        Assert.Null(record.Before);
        Assert.Null(record.After);
        Assert.Null(record.MainReleasedBytes);
        Assert.Null(record.CheckReleasedBytes);
        Assert.Equal("Win32 错误码 5", record.Items[0].Reason);
    }

    [Fact]
    public void 空进程清单_落盘空数组()
    {
        var store = Create();

        Assert.True(store.Append(Report(items: new List<ReleaseItemResult>())).Persisted);

        var line = ReadValidJsonLines(FilePath).Single();
        Assert.Equal(0, line.GetProperty("items").GetArrayLength());
    }

    // —— CASE：Append 空报告（对抗：编排侧异常输入也不得抛出阻塞释放）——

    [Fact]
    public void Append_null报告_返回失败结果不抛异常()
    {
        var store = Create();

        var result = store.Append(null!);

        Assert.False(result.Persisted);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // —— CASE：轮转失败（副本被占用）不丢数据——当前文件保全写入（数据安全优先，同 T-11 口径）——

    [Fact]
    public void 副本被占用轮转失败_记录仍写入当前文件返回成功()
    {
        var store = Create();
        Assert.True(store.Append(Report()).Persisted);
        File.WriteAllText(FilePath, new string('x', 5 * 1024 * 1024));   // 顶到 5MB 触发滚动
        File.WriteAllText(CopyPath(1), "old-copy");                      // 预置 .1 再独占

        using (File.Open(CopyPath(1), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = store.Append(Report());

            Assert.True(result.Persisted);   // 数据保全优先：滚动受阻不牺牲本条留痕
            Assert.False(string.IsNullOrWhiteSpace(result.Error));   // 携带轮转受阻说明
        }

        Assert.Contains("\"items\"", File.ReadAllText(FilePath));   // 新记录在当前文件
    }

    // —— CASE：读侧文件级自愈——副本被独占锁整体跳过，其余文件照常读出（cross-review 确认项）——

    [Fact]
    public void 副本被独占占用_ReadAll跳过该文件其余记录照常读出不抛异常()
    {
        var store = Create();
        Assert.True(store.Append(Report()).Persisted);
        File.WriteAllText(CopyPath(1), "copy-1");

        using (File.Open(CopyPath(1), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var read = store.ReadAll();

            Assert.Single(read.Records);                    // 活动文件记录照常读出
            Assert.Equal(0, read.SkippedCorruptLines);
            Assert.Equal(1, read.SkippedUnavailableFiles);  // .1 整体不可读计数
        }
    }

    // —— CASE：轮转链中途失败的收敛（.2→.3 成功后 .1→.2 受阻；下次 Append 重试轮转恢复满编链）——

    [Fact]
    public void 轮转中途受阻_下次写入重试收敛_记录跨副本仍可读()
    {
        var store = Create();
        Assert.True(store.Append(Report()).Persisted);
        var bigContent = new string('x', 5 * 1024 * 1024);
        File.WriteAllText(FilePath, bigContent);
        File.WriteAllText(CopyPath(1), "copy-1");   // 首轮轮转中被独占锁卡住的一环
        File.WriteAllText(CopyPath(2), "copy-2");   // 首轮轮转应已成功移到 .3

        using (File.Open(CopyPath(1), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = store.Append(Report());
            Assert.True(result.Persisted);   // 数据保全优先：记录仍写入当前文件
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }

        Assert.Equal("copy-2", File.ReadAllText(CopyPath(3)));   // 中途态：.2→.3 已成功

        Assert.True(store.Append(Report()).Persisted);           // 重试轮转 → 收敛
        Assert.Equal("copy-1", File.ReadAllText(CopyPath(2)));   // 链恢复逐级下移
        var read = store.ReadAll();                              // 记录跨副本可读、无整文件丢失
        Assert.Equal(2, read.Records.Count);
        Assert.Equal(0, read.SkippedUnavailableFiles);
    }

    // —— CASE：零值时刻合法记录不因缺 time 判损坏（时区无关，regression：default 哨兵误弃）——

    [Fact]
    public void 零值时刻记录_ReadAll可读回不计损坏()
    {
        Directory.CreateDirectory(_dir);
        var store = Create();
        File.WriteAllText(FilePath,
            """{"time":"0001-01-01T00:00:00Z","before":null,"after":null,"items":[],"mainReleasedBytes":5,"checkReleasedBytes":null}""");

        var read = store.ReadAll();

        var record = Assert.Single(read.Records);
        Assert.Equal(DateTime.MinValue, record.TimeUtc);
        Assert.Equal(5, record.MainReleasedBytes);
        Assert.Equal(0, read.SkippedCorruptLines);
    }

    // —— CASE：默认构造锚定用户数据目录（与 T-11 白名单同目录，提权重启后同目录）——

    [Fact]
    public void 默认构造_锚定用户数据目录()
    {
        var store = new ReleaseLogStore();

        Assert.Equal(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MemRelief", "releases.jsonl"), store.LogFilePath);
        Assert.Equal(ReleaseLogStore.DefaultDataDirectory(), WhitelistStore.DefaultDataDirectory());
    }
}
