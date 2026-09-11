using MemRelief.Core.Contracts;
using MemRelief.Core.Storage;

namespace MemRelief.App.Tests.TestDoubles;

/// <summary>IRulePackStore 测试替身：返回固定名单包（无失败）。仅测试程序集使用。</summary>
public sealed class StaticRulePackStore : IRulePackStore
{
    public RulePackLoadResult LoadRulePack() => new(
        new RulePack(["crashpad"], [], [], []), []);
}
