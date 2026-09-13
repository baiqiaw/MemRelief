using MemRelief.Core.Contracts;
using MemRelief.Core.Storage;

namespace MemRelief.App.Tests.TestDoubles;

/// <summary>
/// IWhitelistStore 测试替身（App.Tests 共用）：内存态条目表，Add/Remove 可注入失败与自愈记录，
/// Add/Remove 调用记录入参供加白/移除链路断言。仅测试程序集使用，非生产代码。
/// 并发语义对齐真实 WhitelistStore（内部锁串行化读写）——并发测试用例须测生产同款形态。
/// </summary>
public sealed class FakeWhitelistStore : IWhitelistStore
{
    private readonly object _gate = new();
    private readonly List<WhitelistEntry> _entries = [];

    public WhitelistRecovery? Recovery { get; set; }

    public int AddCount { get; private set; }

    public List<string> AddedNames { get; } = [];

    public List<string> RemovedNames { get; } = [];

    /// <summary>注入后每次 Add 均抛出该异常（盘写失败语义），内存态与计数器不受异常影响（失败前置增）。</summary>
    public Exception? OnAddError { get; set; }

    /// <summary>注入后每次 Remove 均抛出该异常（盘写失败语义），内存态与计数器不受异常影响（失败前置增）。</summary>
    public Exception? OnRemoveError { get; set; }

    /// <summary>预置存储条目（白名单面板/移除链路铺态用）。</summary>
    public void Seed(params WhitelistEntry[] entries)
    {
        lock (_gate) _entries.AddRange(entries);
    }

    public WhitelistSnapshot Snapshot()
    {
        lock (_gate) return new WhitelistSnapshot(_entries.ToArray());
    }

    public IReadOnlyList<WhitelistEntry> List()
    {
        lock (_gate) return _entries.ToArray();
    }

    public WhitelistEntry Add(string name, string? path = null, string? note = null)
    {
        AddCount++;
        lock (AddedNames) AddedNames.Add(name);
        if (OnAddError is not null)
        {
            throw OnAddError;
        }

        var entry = new WhitelistEntry(name, DateTime.UtcNow, path, note);
        lock (_gate) _entries.Add(entry);
        return entry;
    }

    public bool Remove(string name)
    {
        lock (RemovedNames) RemovedNames.Add(name);
        if (OnRemoveError is not null)
        {
            throw OnRemoveError;
        }

        lock (_gate)
        {
            var removed = _entries.RemoveAll(e =>
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            return removed > 0;
        }
    }
}
