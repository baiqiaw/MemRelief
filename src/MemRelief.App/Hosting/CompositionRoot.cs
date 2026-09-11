using MemRelief.App.Scanning;
using MemRelief.App.State;
using MemRelief.App.ViewModels;
using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using MemRelief.Core.Scanner;
using MemRelief.Core.Storage;

namespace MemRelief.App.Hosting;

/// <summary>
/// 组合根（system §3.1）：App 构造并注入 Core 全部模块，唯一依赖方向 App → Core。
/// 自建对象图，不引 DI 容器（对象量小、构造关系稳定，容器属惯例性抽象）。
/// </summary>
public static class CompositionRoot
{
    /// <summary>构造扫描/判定链服务对象图（不含窗口，供非 STA 上下文测试）。</summary>
    public static MainViewModel CreateViewModel()
    {
        // scanner 模块：快照/验签/概览（T-01/T-02/T-04/T-05，接口已冻结）
        IScanner scanner = new Scanner();

        // rules 模块：三级判定（T-06/T-07，纯函数）
        IRulesEngine rules = new RulesEngine();

        // storage 模块：内置名单装载（T-13 真实实现）
        IRulePackStore rulePackStore = new RulePackStore();

        // 编排方环境参数（契约：纯函数约束下环境信息一律参数注入，data-contracts §1.1）
        var context = new ClassificationContext(Environment.ProcessId, Environment.UserName);

        // 白名单快照：storage 的白名单接线随 T-15/T-16/T-26 落地（WBS T-14 交付物行既有口径），
        // 本包传空快照——这是既定范围边界，不含白名单假实现代码
        var whitelist = new WhitelistSnapshot([]);

        var coordinator = new ScanCoordinator(scanner, rules, rulePackStore, context, whitelist);
        return new MainViewModel(new UiStateMachine(), coordinator, scanner);
    }

    /// <summary>构造主窗口（含 ViewModel 接线；STA 上下文调用）。</summary>
    public static MainWindow CreateMainWindow() => new(CreateViewModel());
}
