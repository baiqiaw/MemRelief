using System.Windows;
using System.Windows.Threading;
using MemRelief.App.Releasing;
using MemRelief.App.Scanning;
using MemRelief.App.State;
using MemRelief.App.ViewModels;
using MemRelief.Core.Contracts;
using MemRelief.Core.Releaser;
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
    /// <summary>
    /// 构造扫描/判定/释放链服务对象图（不含窗口，供非 STA 上下文测试）。
    /// 释放域依赖（T-16）：releaser/日志存储/确认弹窗/重启器/编组通道/重启失败项/关窗动作，
    /// 缺省=不启用对应编排（测试便利；生产经 <see cref="CreateMainWindow"/> 全量注入）。
    /// </summary>
    public static MainViewModel CreateViewModel(
        IScanner? scanner = null,
        IReleaser? releaser = null,
        IReleaseLogStore? logStore = null,
        IReleaseConfirmDialog? confirmDialog = null,
        IAppRestarter? restarter = null,
        Action<Action>? marshal = null,
        IReadOnlyList<RestartFailedItem>? restartFailedItems = null,
        Action? shutdown = null)
    {
        // scanner 模块：快照/验签/概览（T-01/T-02/T-04/T-05，接口已冻结）；
        // 可注入以供主窗口与概览条共享同实例（概览条子 VM 独立采样，#38 步骤 2 收口后主 VM 不持 scanner）
        IScanner scannerInstance = scanner ?? new Scanner();

        // rules 模块：三级判定（T-06/T-07，纯函数）
        IRulesEngine rules = new RulesEngine();

        // storage 模块：内置名单装载（T-13 真实实现）
        IRulePackStore rulePackStore = new RulePackStore();

        // storage 模块：白名单存储（T-11 真实实现，T-15 接线——构造即装载，损坏自愈经 Recovery 通道提示）
        IWhitelistStore whitelistStore = new WhitelistStore();

        // storage 模块：释放日志存储（T-12 真实实现；T-16 起经参数透传——Append 编排与
        // "打开日志/数据目录"入口（T-26，以 LogFilePath 为锚点，与白名单同目录=用户数据目录）
        // 共用同一实例；缺省 null=两类编排均不启用，生产经 CreateMainWindow 全量注入）

        // 编排方环境参数（契约：纯函数约束下环境信息一律参数注入，data-contracts §1.1）
        var context = new ClassificationContext(Environment.ProcessId, Environment.UserName);

        // 白名单以提供者注入：每次判定取当前一致视图（加白即时重判生效，③.s4 裁决⑥）
        var coordinator = new ScanCoordinator(
            scannerInstance, rules, rulePackStore, context, () => whitelistStore.Snapshot());
        return new MainViewModel(
            new UiStateMachine(), coordinator, rules, whitelistStore,
            releaser, logStore, confirmDialog, restarter, marshal, restartFailedItems, shutdown);
    }

    /// <summary>
    /// 构造主窗口（含 ViewModel 全量接线；STA 上下文调用）。
    /// 概览条子 VM 与主 VM 共享 Scanner 与状态机实例（#38 步骤 1 收口）；
    /// 释放域接线（T-16）：ProcessReleaser+日志存储+MessageBox 确认弹窗+提权重启器+
    /// Dispatcher 编组通道+重启失败项高亮清单（T-27 重启参数解析产物，可空）。
    /// </summary>
    public static MainWindow CreateMainWindow(RestartOptions? restart = null)
    {
        var scanner = new Scanner();
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        var main = CreateViewModel(
            scanner,
            releaser: new ProcessReleaser(),
            logStore: new ReleaseLogStore(),
            confirmDialog: new MessageBoxReleaseConfirmDialog(),
            restarter: new ElevationRestarter(),
            marshal: action => dispatcher.BeginInvoke(action),
            restartFailedItems: restart?.FailedItems,
            shutdown: () => Application.Current?.Shutdown());
        return new MainWindow(main, new OverviewBarViewModel(scanner, main.StateMachine));
    }
}
