using MemRelief.Core.Contracts;
using MemRelief.Core.Storage;

namespace MemRelief.App.Tests.TestDoubles;

/// <summary>
/// IWhitelistStore 测试替身（App.Tests 共用）：内存态条目表，Add/Remove 可注入失败与自愈记录，
/// Add 调用记录入参供加白链路断言。仅测试程序集使用，非生产代码。
/// </summary>
public sealed class FakeWhitelistStore : IWhitelistStore
{
    private readonly List<WhitelistEntry> _entries = [];

    public WhitelistRecovery? Recovery { get; set; }

    public int AddCount { get; private set; }

    public List<string> AddedNames { get; } = [];

    /// <summary>注入后每次 Add 均抛出该异常（盘写失败语义），内存态与计数器不受异常影响（失败前置增）。</summary>
    public Exception? OnAddError { get; set; }

    public WhitelistSnapshot Snapshot() => new(_entries.ToArray());

    public IReadOnlyList<WhitelistEntry> List() => _entries.ToArray();

    public WhitelistEntry Add(string name, string? path = null, string? note = null)
    {
        AddCount++;
        AddedNames.Add(name);
        if (OnAddError is not null)
        {
            throw OnAddError;
        }

        var entry = new WhitelistEntry(name, DateTime.UtcNow, path, note);
        _entries.Add(entry);
        return entry;
    }

    public bool Remove(string name)
    {
        var removed = _entries.RemoveAll(e =>
            string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        return removed > 0;
    }
}
