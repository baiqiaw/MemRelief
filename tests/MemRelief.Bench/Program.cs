using System.Text;
using MemRelief.Bench;
using MemRelief.Core.Contracts;
using MemRelief.Core.Rules;
using MemRelief.Core.Scanner;
using MemRelief.Core.Storage;

// T-21 真机扫描验证台入口（issue #23）：驱动四步扫描链，输出分级明细 + 判定依据 + 分段计时。
// 用法: dotnet MemRelief.Bench.dll [--top N] [--repeat K]
//   --top N：谨慎/受保护级明细行数（默认 20）
//   --repeat K：同进程连扫轮数（默认 1，上限 10）；轮 1 冷缓存，轮 2 起缓存热（与 App 常驻复扫同构）
// 只做驱动与输出；推荐准确性人工复核方法见脚本输出第 [3]/[4] 段与 scripts/baseline-result.txt。
// 退出码：0=测量完成且计时可信；2=测量完成但存在计时异常（报告文本同步标记）；1=扫描链异常（原因走 stderr）。
Console.OutputEncoding = Encoding.UTF8;

try
{
    return await RunAsync(args);
}
catch (Exception ex)
{
    // 与 App 侧 ScanCoordinator 同口径收口：类型名前缀保底（部分 COM/WMI 包装异常 Message 为空串）。
    // 验证台 fail-fast 但不静默：已产出的前几轮报告已逐轮落 stdout，失败原因收口到 stderr。
    var reason = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : $"{ex.GetType().Name}: {ex.Message}";
    await Console.Error.WriteLineAsync($"验证台扫描链失败：{reason}");
    return 1;
}

static async Task<int> RunAsync(string[] args)
{
    var topN = BenchCli.ParseTop(args);
    var repeat = BenchCli.ParseRepeat(args);

    // 组合根（自建 Core 对象图，与 App 平行的独立消费者，不依赖 App 工程）：
    // 名单装载失败兜底与 ScanCoordinator 同款（RulePack.Empty，system 法-3）。
    IRulePackStore rulePackStore = new RulePackStore();
    IWhitelistStore whitelistStore = new WhitelistStore();   // 构造即装载；损坏自愈经 Recovery 通道提示
    if (whitelistStore.Recovery is { } recovery)
    {
        // 文案对齐 MainViewModel 先例：禁固定事实断言（备份失败变体原文件原地保留，与已重置变体事实不同）
        Console.WriteLine($"白名单异常已处理：{recovery.Reason}");
    }

    var runner = new BenchRunner(
        new Scanner(),
        new RulesEngine(),
        rulePackProvider: () => rulePackStore.LoadRulePack(),
        whitelistProvider: () => whitelistStore.Snapshot(),
        context: new ClassificationContext(Environment.ProcessId, Environment.UserName));

    var anomaliesSeen = false;
    for (var round = 1; round <= repeat; round++)
    {
        var roundStartedAt = DateTime.Now;   // 采样时点=本轮扫描开始（溯源口径；扫描完成后取值会混入扫描耗时）
        var result = await runner.RunAsync();
        anomaliesSeen |= result.TimingAnomalies().Count > 0;
        var env = new BenchEnvironment(Environment.MachineName, roundStartedAt);
        // 多轮时轮 2 起只出计时表（明细各轮高度重复，单轮完整报告已承载）
        var text = round == 1
            ? BenchReportFormatter.Format(result, env, topN)
            : BenchReportFormatter.FormatTimings(result, env, round);
        Console.Write(text);
    }

    // 计时异常（秒表回退/时钟矛盾）对自动化不可见的防御：非零退出码作机器可读信号（报告文本已同步标记）
    return anomaliesSeen ? 2 : 0;
}
