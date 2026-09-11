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
    public static MainViewModel CreateViewModel(IScanner? scanner = null)
    {
        // scanner 模块：快照/验签/概览（T-01/T-02/T-04/T-05，接口已冻结）；可注入以供主窗口与概览条共享同实例
        IScanner scannerInstance = scanner ?? new Scanner();

        // rules 模块：三级判定（T-06/T-07，纯函数）
        IRulesEngine rules = new RulesEngine();

        // storage 模块：内置名单装载（T-13 真实实现）
        IRulePackStore rulePackStore = new RulePackStore();

        // storage 模块：白名单存储（T-11 真实实现，T-15 接线——构造即装载，损坏自愈经 Recovery 通道提示）
        IWhitelistStore whitelistStore = new WhitelistStore();

        // 编排方环境参数（契约：纯函数约束下环境信息一律参数注入，data-contracts §1.1）
        var context = new ClassificationContext(Environment.ProcessId, Environment.UserName);

        // 白名单以提供者注入：每次判定取当前一致视图（加白即时重判生效，③.s4 裁决⑥）
        var coordinator = new ScanCoordinator(
            scannerInstance, rules, rulePackStore, context, () => whitelistStore.Snapshot());
        return new MainViewModel(new UiStateMachine(), coordinator, scannerInstance, rules, whitelistStore);
    }

    /// <summary>构造主窗口（含 ViewModel 接线；STA 上下文调用）。概览条子 VM 与主 VM 共享 Scanner 与状态机实例（#38 收口）。</summary>
    public static MainWindow CreateMainWindow()
    {
        var scanner = new Scanner();
        var main = CreateViewModel(scanner);
        return new MainWindow(main, new OverviewBarViewModel(scanner, main.StateMachine));
    }
}
