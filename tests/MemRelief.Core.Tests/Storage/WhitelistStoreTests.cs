using MemRelief.Core.Contracts;
using MemRelief.Core.Storage;
using Xunit;

namespace MemRelief.Core.Tests.Storage;

// 白名单存储单测（T-11）：损坏自愈 GWT（R04）、Add/Remove/List/Snapshot+元数据、
// 不可变一致快照、写穿持久化、用户数据目录锚定、并发与写失败对抗（AC 见 issue #14）
public class WhitelistStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "memrelief-t11-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "whitelist.json");
    private string CorruptPath => FilePath + ".corrupt";

    private WhitelistStore Create() => new(_dir);

    private void WriteFile(string content) =>
        File.WriteAllText(FilePath, content);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // —— CASE：损坏自愈 GWT（AC：损坏→改名 .corrupt 保留+重建空白名单；PRD §3.7）——

    [Theory]
    [InlineData("""[{"name":"a.exe","addedAtUtc":"2026-09-01T00:00:00Z""")]   // 截断
    [InlineData("{\"name\":")]                                                // 半行 JSON
    [InlineData("这不是 JSON")]                                               // 乱码文本
    [InlineData("[\"plain-string\"]")]                                        // 元素形状错（非条目对象）
    public void 文件损坏_构造时改名corrupt备份并重建空白(string content)
    {
        Directory.CreateDirectory(_dir);
        WriteFile(content);

        var store = Create();

        Assert.Empty(store.Snapshot().Entries);
        Assert.Empty(store.List());
        Assert.True(File.Exists(CorruptPath), ".corrupt 备份应存在");
        Assert.Equal(content, File.ReadAllText(CorruptPath));
        Assert.True(File.Exists(FilePath), "应重建空白名单文件");
        Assert.Equal("[]", File.ReadAllText(FilePath));
        var recovery = Assert.IsType<WhitelistRecovery>(store.Recovery);
        Assert.Equal(CorruptPath, recovery.BackupPath);
        Assert.False(string.IsNullOrWhiteSpace(recovery.Reason));
    }

    [Fact]
    public void 文件损坏_非法UTF8字节_同款自愈()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(FilePath, [0xFF, 0xFE, 0x00, 0x01]);

        var store = Create();

        Assert.Empty(store.Snapshot().Entries);
        Assert.True(File.Exists(CorruptPath));
        Assert.Equal([0xFF, 0xFE, 0x00, 0x01], File.ReadAllBytes(CorruptPath));
        Assert.NotNull(store.Recovery);
    }

    [Fact]
    public void 文件损坏_JSON对象非数组_按损坏自愈()
    {
        Directory.CreateDirectory(_dir);
        WriteFile("""{"name":"a.exe"}""");

        var store = Create();

        Assert.Empty(store.Snapshot().Entries);
        Assert.True(File.Exists(CorruptPath));
        Assert.NotNull(store.Recovery);
    }

    [Fact]
    public void 已有旧corrupt_再次损坏_覆盖保留最新备份()
    {
        Directory.CreateDirectory(_dir);
        WriteFile("第一份损坏");
        File.WriteAllText(CorruptPath, "旧备份");
        File.WriteAllText(FilePath, "第二份损坏");

        var store = Create();

        Assert.Equal("第二份损坏", File.ReadAllText(CorruptPath));
    }

    [Fact]
    public void 文件不存在_空白名单_无恢复记录()
    {
        Directory.CreateDirectory(_dir);

        var store = Create();

        Assert.Empty(store.Snapshot().Entries);
        Assert.Null(store.Recovery);
        // 缺失≠损坏：不产生备份文件
        Assert.False(File.Exists(CorruptPath));
    }

    [Fact]
    public void 合法文件_正常装载_不产生corrupt与恢复记录()
    {
        Directory.CreateDirectory(_dir);
        WriteFile("""[{"name":"a.exe","addedAtUtc":"2026-09-01T08:00:00Z","path":"C:\\a.exe","note":"备注"}]""");

        var store = Create();

        var entry = Assert.Single(store.List());
        Assert.Equal("a.exe", entry.Name);
        Assert.Equal(new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), entry.AddedAtUtc);
        Assert.Equal(@"C:\a.exe", entry.Path);
        Assert.Equal("备注", entry.Note);
        Assert.Null(store.Recovery);
        Assert.False(File.Exists(CorruptPath));
    }

    // —— CASE：装载防御（条目级瑕疵剔除，形状级损坏才走自愈；同 RulePackStore 防御风格）——

    [Fact]
    public void 条目瑕疵_null元素与空白名剔除_其余保留()
    {
        Directory.CreateDirectory(_dir);
        WriteFile("""[null,{"name":"  ","addedAtUtc":"2026-09-01T00:00:00Z"},{"name":"ok.exe","addedAtUtc":"2026-09-01T00:00:00Z"}]""");

        var store = Create();

        var entry = Assert.Single(store.List());
        Assert.Equal("ok.exe", entry.Name);
        Assert.Null(store.Recovery);    // 条目级清洗不是损坏
    }

    [Fact]
    public void 条目级畸形_坏元素剔除保留有效条目_非损坏()
    {
        Directory.CreateDirectory(_dir);
        // 混入：字符串元素、坏时间戳条目（条目级畸形各自剔除，不拖垮整份名单）
        WriteFile("""["junk-string",{"name":"bad-time.exe","addedAtUtc":"garbage"},{"name":"ok.exe","addedAtUtc":"2026-09-01T00:00:00Z"}]""");

        var store = Create();

        var entry = Assert.Single(store.List());
        Assert.Equal("ok.exe", entry.Name);
        Assert.Null(store.Recovery);
    }

    [Fact]
    public void 非空数组但零有效条目_按损坏自愈()
    {
        Directory.CreateDirectory(_dir);
        WriteFile("""["junk1","junk2"]""");

        var store = Create();

        Assert.Empty(store.Snapshot().Entries);
        Assert.True(File.Exists(CorruptPath));
        Assert.NotNull(store.Recovery);
    }

    [Fact]
    public void 超过装载大小上限_按损坏自愈备份保留()
    {
        Directory.CreateDirectory(_dir);
        // 1MB+ 超限文件（防御误粘超大文件拖垮启动；走既有自愈语义，备份可找回）
        WriteFile("[" + new string('x', 1024 * 1024 + 1) + "]");

        var store = Create();

        Assert.Empty(store.Snapshot().Entries);
        Assert.True(File.Exists(CorruptPath));
        Assert.NotNull(store.Recovery);
        Assert.Contains("1MB", store.Recovery!.Reason);
    }

    // —— CASE：损坏自愈降级路径（评审 A：备份失败不重建——防覆盖未备份的原文件）——

    [Fact]
    public void 备份失败_原文件原地保留_不重建_记录待重试()
    {
        Directory.CreateDirectory(_dir);
        const string corruptContent = "损坏内容待备份";
        WriteFile(corruptContent);
        File.WriteAllText(CorruptPath, "旧备份");
        File.SetAttributes(CorruptPath, FileAttributes.ReadOnly);   // 只读备份 → Move 覆盖失败
        try
        {
            var store = Create();

            Assert.Equal(corruptContent, File.ReadAllText(FilePath));   // 原文件未被覆盖重建
            Assert.Equal("旧备份", File.ReadAllText(CorruptPath));      // 旧备份未被覆盖
            Assert.Empty(store.Snapshot().Entries);                     // fail-closed：装载失败不豁免任何进程
            var recovery = Assert.IsType<WhitelistRecovery>(store.Recovery);
            Assert.Contains("备份失败", recovery.Reason);
        }
        finally
        {
            File.SetAttributes(CorruptPath, FileAttributes.Normal);     // 还原属性以便清理
        }
    }

    [Fact]
    public void 主文件缺失但corrupt在_恢复记录提示数据在备份中()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CorruptPath, """[{"name":"lost.exe","addedAtUtc":"2026-09-01T00:00:00Z"}]""");

        var store = Create();

        Assert.Empty(store.Snapshot().Entries);
        var recovery = Assert.IsType<WhitelistRecovery>(store.Recovery);
        Assert.Equal(CorruptPath, recovery.BackupPath);
        Assert.Contains("自愈未完成", recovery.Reason);
    }

    [Fact]
    public void 条目名称带空白_Trim后装载()
    {
        Directory.CreateDirectory(_dir);
        WriteFile("""[{"name":"  pad.exe  ","addedAtUtc":"2026-09-01T00:00:00Z"}]""");

        var store = Create();

        Assert.Equal("pad.exe", Assert.Single(store.List()).Name);
    }

    [Fact]
    public void 条目时间缺省或本地时区_归一为Utc()
    {
        Directory.CreateDirectory(_dir);
        // addedAtUtc 缺省 → default；带 +08:00 偏移 → 转 Utc
        WriteFile("""[{"name":"a.exe"},{"name":"b.exe","addedAtUtc":"2026-09-01T16:00:00+08:00"}]""");

        var entries = Create().List().OrderBy(e => e.Name).ToList();

        Assert.Equal(DateTimeKind.Utc, entries[0].AddedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, entries[1].AddedAtUtc.Kind);
        Assert.Equal(new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), entries[1].AddedAtUtc);
    }

    // —— CASE：Add/Remove/List/Snapshot + 元数据（AC：CRUD 全量）——

    [Fact]
    public void Add_返回条目含Utc时间与元数据_List与Snapshot可见()
    {
        var store = Create();
        var before = DateTime.UtcNow;

        var added = store.Add("game.exe", @"C:\game\game.exe", "常驻挂机");

        Assert.Equal("game.exe", added.Name);
        Assert.Equal(@"C:\game\game.exe", added.Path);
        Assert.Equal("常驻挂机", added.Note);
        Assert.Equal(DateTimeKind.Utc, added.AddedAtUtc.Kind);
        Assert.InRange(added.AddedAtUtc, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));

        Assert.Equal(added, Assert.Single(store.List()));
        Assert.True(store.Snapshot().ContainsName("game.exe"));
    }

    [Fact]
    public void Snapshot_ContainsName_大小写不敏感()
    {
        var store = Create();
        store.Add("Game.EXE");

        var snapshot = store.Snapshot();
        Assert.True(snapshot.ContainsName("game.exe"));
        Assert.True(snapshot.ContainsName("GAME.exe"));
        Assert.False(snapshot.ContainsName("game2.exe"));
    }

    [Fact]
    public void Remove_命中返回真_List与Snapshot同步移除()
    {
        var store = Create();
        store.Add("a.exe");

        Assert.True(store.Remove("A.EXE"));   // 匹配键 OrdinalIgnoreCase

        Assert.Empty(store.List());
        Assert.False(store.Snapshot().ContainsName("a.exe"));
    }

    [Fact]
    public void Remove_未命中返回假()
    {
        var store = Create();
        store.Add("a.exe");

        Assert.False(store.Remove("b.exe"));
        Assert.Single(store.List());
    }

    [Fact]
    public void Add_同名不同大小写_幂等_保留首次添加时间与元数据()
    {
        var store = Create();
        var first = store.Add("a.exe", @"C:\old\a.exe", "首次");

        var second = store.Add("A.EXE", @"C:\new\a.exe", "再次");

        Assert.Equal(first, second);          // 同一匹配键只保留一条
        Assert.Single(store.List());
        Assert.Equal("首次", Assert.Single(store.List()).Note);
    }

    [Fact]
    public void Add_名称两端空白_Trim后存储()
    {
        var store = Create();

        var added = store.Add("  pad.exe  ");

        Assert.Equal("pad.exe", added.Name);
        Assert.True(store.Snapshot().ContainsName("pad.exe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Add_空白名_拒绝(string name)
    {
        var store = Create();

        Assert.Throws<ArgumentException>(() => store.Add(name));
        Assert.Empty(store.List());
    }

    // —— CASE：快照不可变一致视图（AC；对抗：快照后变更源不相互影响）——

    [Fact]
    public void 快照后Add_旧快照不变()
    {
        var store = Create();
        store.Add("a.exe");
        var snapshot = store.Snapshot();

        store.Add("b.exe");

        Assert.Single(snapshot.Entries);                       // 旧快照不受影响
        Assert.False(snapshot.ContainsName("b.exe"));
        Assert.Equal(2, store.Snapshot().Entries.Count);       // 新快照反映变更
    }

    [Fact]
    public void 快照后Remove_旧快照不变()
    {
        var store = Create();
        store.Add("a.exe");
        var snapshot = store.Snapshot();

        store.Remove("a.exe");

        Assert.True(snapshot.ContainsName("a.exe"));           // 旧快照仍一致
        Assert.Empty(store.Snapshot().Entries);
    }

    [Fact]
    public void List返回集合_与内部状态隔离()
    {
        var store = Create();
        store.Add("a.exe");
        var listed = store.List();

        store.Add("b.exe");

        Assert.Single(listed);                                 // 早先拿到的引用不变
    }

    // —— CASE：写穿持久化（AC：本地持久化；对抗：跨实例可见、无 tmp 残留）——

    [Fact]
    public void Add_写穿落盘_新实例装载可见()
    {
        var store = Create();
        store.Add("a.exe", null, "备注");

        var reloaded = Create();

        var entry = Assert.Single(reloaded.List());
        Assert.Equal("a.exe", entry.Name);
        Assert.Equal("备注", entry.Note);
        Assert.Equal(store.List()[0].AddedAtUtc, entry.AddedAtUtc);
    }

    [Fact]
    public void Remove_写穿落盘_新实例装载已除()
    {
        var store = Create();
        store.Add("a.exe");
        store.Add("b.exe");
        store.Remove("a.exe");

        var reloaded = Create();

        Assert.Equal("b.exe", Assert.Single(reloaded.List()).Name);
    }

    [Fact]
    public void Add与Remove后_无临时文件残留()
    {
        var store = Create();

        store.Add("a.exe");
        store.Remove("a.exe");

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void 目录不存在_Add时创建并落盘()
    {
        Assert.False(Directory.Exists(_dir));

        Create().Add("a.exe");

        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void 落盘schema_条目数组含四字段_空字段显式null()
    {
        var store = Create();

        store.Add("a.exe", @"C:\x\a.exe", "注");
        store.Add("b.exe");                                 // path/note 缺省

        var json = File.ReadAllText(FilePath);
        Assert.Contains("\"name\"", json);                  // camelCase 字段名
        Assert.Contains("\"addedAtUtc\"", json);
        Assert.Contains("\"path\": null", json);            // schema 固定四字段，空字段显式 null（口径稳定）
        Assert.Contains("\"note\": null", json);
        // 值回读验证（路径含反斜杠，盘上为 JSON 转义形态，文本断言不直观）
        var reread = Create();
        Assert.Equal(@"C:\x\a.exe", reread.List().First(e => e.Name == "a.exe").Path);
    }

    // —— CASE：用户数据目录锚定（AC：提权重启后同目录）——

    [Fact]
    public void 默认数据目录_锚定LocalAppData_MemRelief()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemRelief");

        Assert.Equal(expected, WhitelistStore.DefaultDataDirectory());
    }

    // —— CASE：写失败对抗（先盘后内存：盘写失败内存不动、tmp 清理）——

    [Fact]
    public void 目标文件被独占占用_Add抛异常且内存不变_无tmp残留()
    {
        Directory.CreateDirectory(_dir);
        var store = Create();
        store.Add("a.exe");                                    // 先落一份，使目标文件存在
        var snapshotBefore = store.Snapshot();

        using (File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => store.Add("b.exe"));
        }

        Assert.Equal(snapshotBefore.Entries.Count, store.Snapshot().Entries.Count);
        Assert.False(store.Snapshot().ContainsName("b.exe"));  // 内存未被污染
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));       // 失败路径清理 tmp
    }

    [Fact]
    public void 目标文件被独占占用_Remove抛异常且内存不变()
    {
        Directory.CreateDirectory(_dir);
        var store = Create();
        store.Add("a.exe");
        var snapshotBefore = store.Snapshot();

        using (File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => store.Remove("a.exe"));
        }

        Assert.True(store.Snapshot().ContainsName("a.exe"));   // 内存未被污染
    }

    // —— CASE：并发对抗（Add/Remove 与 Snapshot 并行，终态一致且落盘完整）——

    [Fact]
    public void 并发Add不同名_终态全部在内存与盘()
    {
        var store = Create();
        const int threads = 4, perThread = 5;

        Task.WaitAll(Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
                store.Add($"proc-t{t}-{i}.exe");
        })).ToArray());

        Assert.Equal(threads * perThread, store.List().Count);
        // 写穿串行化后盘上文件可完整解析，条目数一致
        var reloaded = Create();
        Assert.Equal(threads * perThread, reloaded.List().Count);
    }

    [Fact]
    public void 并发读写快照_读侧恒得一致视图不抛异常()
    {
        var store = Create();
        using var stop = new CancellationTokenSource();
        var reads = 0;   // 迭代计数：防 reader 未被调度零执行空转通过

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                store.Add($"p{i}.exe");
                store.Remove($"p{i}.exe");
            }
        });
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var snapshot = store.Snapshot();
                Assert.All(snapshot.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Name)));
                Interlocked.Increment(ref reads);
            }
        });

        writer.Wait();
        stop.Cancel();
        reader.Wait();
        Assert.Empty(store.List());
        Assert.True(Volatile.Read(ref reads) > 0, "并发读循环应至少执行一次（防空转通过）");
    }
}
