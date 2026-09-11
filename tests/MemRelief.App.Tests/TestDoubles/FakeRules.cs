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

    public List<(ScanResult Scan, RulePack Pack)> ClassifyCalls { get; } = [];

    public Task<IReadOnlyList<Classification>> Classify(
        ScanResult scan, WhitelistSnapshot whitelist, RulePack rulePack, ClassificationContext context)
    {
        ClassifyCalls.Add((scan, rulePack));
        return Task.FromResult(Result);
    }

    public ISet<int> CandidateIds(ScanResult scan) => ReturnedCandidates;

    public IReadOnlyList<QueryResult> Query(
        ScanResult scan, IReadOnlyList<Classification> classifications, string? name, int? pid) => [];
}
