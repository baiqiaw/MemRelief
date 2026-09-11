using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using MemRelief.Core.Storage;

namespace MemRelief.App.Tests.TestDoubles;

/// <summary>
/// IRulesEngine 测试替身（App.Tests 共用）：默认返回一条推荐级分类（孤儿命中），
/// Result/ReturnedCandidates 可按用例覆写；ClassifyCalls 记录入参供编排契约断言。
/// 仅测试程序集使用，非生产代码。
/// </summary>
public sealed class FakeRules : IRulesEngine
{
    public IReadOnlyList<Classification> Result { get; set; } =
    [
        new Classification(100, Level.Recommend, [new Basis(1, "孤儿")], 60_000_000, null, [], false),
    ];

    public ISet<int> ReturnedCandidates { get; set; } = new HashSet<int> { 100 };

    public List<(ScanResult Scan, WhitelistSnapshot Whitelist, RulePack Pack)> ClassifyCalls { get; } = [];

    /// <summary>注入后 Classify 抛出该异常（重判失败语义），供失败链分流测试。</summary>
    public Exception? OnClassifyError { get; set; }

    /// <summary>按调用序（1 起）挂起第 N 次 Classify（竞态时序测试）；null=不挂起。</summary>
    public Func<int, Task>? OnClassifyGate { get; set; }

    /// <summary>按调用序（1 起）选择返回结果（竞态时序测试）；null=恒返回 <see cref="Result"/>。</summary>
    public Func<int, IReadOnlyList<Classification>>? ResultSelector { get; set; }

    /// <summary>Query 结果覆写（默认空）；入参记录见 <see cref="QueryCalls"/>。</summary>
    public Func<ScanResult, IReadOnlyList<Classification>, string?, int?, IReadOnlyList<QueryResult>>? OnQuery { get; set; }

    public List<(ScanResult Scan, IReadOnlyList<Classification> Classifications, string? Name, int? Pid)> QueryCalls { get; } = [];

    private int _classifyIndex;

    public async Task<IReadOnlyList<Classification>> Classify(
        ScanResult scan, WhitelistSnapshot whitelist, RulePack rulePack, ClassificationContext context)
    {
        var index = ++_classifyIndex;
        ClassifyCalls.Add((scan, whitelist, rulePack));
        if (OnClassifyError is not null)
        {
            throw OnClassifyError;
        }

        if (OnClassifyGate is not null)
        {
            await OnClassifyGate(index);
        }

        return ResultSelector is not null ? ResultSelector(index) : Result;
    }

    public ISet<int> CandidateIds(ScanResult scan) => ReturnedCandidates;

    public IReadOnlyList<QueryResult> Query(
        ScanResult scan, IReadOnlyList<Classification> classifications, string? name, int? pid)
    {
        QueryCalls.Add((scan, classifications, name, pid));
        return OnQuery is null ? [] : OnQuery(scan, classifications, name, pid);
    }
}
