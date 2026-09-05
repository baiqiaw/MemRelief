using MemRelief.Core.Contracts;

namespace MemRelief.Core.Rules;

/// <summary>判定引擎（rules 模块）。T-06 范围：Classify 主链（含服务/受拒预标）；CandidateIds/Query/跨用户预标归 T-07。</summary>
public interface IRulesEngine
{
    Task<IReadOnlyList<Classification>> Classify(
        ScanResult scan,
        WhitelistSnapshot whitelist,
        RulePack rulePack,
        ClassificationContext context);
}

/// <summary>
/// 三级判定与冲突消解引擎。
/// 法条（rules.md §6）：纯函数——同输入必得同输出，零 I/O 零可变状态；依据完整可追溯；
/// 白名单完全排除；树合计唯一承载于 Classification.TreePrivateBytes；
/// 保护性判定所需数据缺失（SignalFailure/名单缺失/null 语义）→ 不进✅级（system 法-3）。
/// 判定语义唯一事实源 = PRD F1 口径表 #1–#15。
/// 依据编号：SignalId = 口径表编号；0 = 非口径表依据保留值（兜底/本工具自身/系统保护穷举名/名单不可用）。
/// </summary>
public sealed class RulesEngine : IRulesEngine
{
    // 口径 #13：小体量降级阈值（PRD 建议值；可调性归 ui 设置面，v1 引擎内常量）
    private const long SmallTreeThresholdBytes = 50 * 1024 * 1024;

    // 口径 #7：CPU 差分显著阈值（≤3s 扫描窗口内 >1s CPU 时间）
    private const double CpuDeltaThresholdSeconds = 1.0;

    public Task<IReadOnlyList<Classification>> Classify(
        ScanResult scan,
        WhitelistSnapshot whitelist,
        RulePack rulePack,
        ClassificationContext context)
    {
        IReadOnlyList<Classification> result = ClassifyCore(scan, whitelist, rulePack, context);
        return Task.FromResult(result);
    }

    private static IReadOnlyList<Classification> ClassifyCore(
        ScanResult scan,
        WhitelistSnapshot whitelist,
        RulePack pack,
        ClassificationContext ctx)
    {
        var treeBytes = ComputeTreePrivateBytes(scan.Snapshots);
        var orphanPids = CollectOrphanPids(scan.Snapshots);
        var hasGlobalFailure = scan.Failures.Any(f => !f.Pid.HasValue);
        var failuresByPid = scan.Failures
            .Where(f => f.Pid.HasValue)
            .GroupBy(f => f.Pid!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<Classification>(scan.Snapshots.Count);
        foreach (var p in scan.Snapshots)
        {
            failuresByPid.TryGetValue(p.Pid, out var failures);
            result.Add(ClassifyOne(p, whitelist, pack, ctx, hasGlobalFailure, failures,
                treeBytes.GetValueOrDefault(p.Pid), orphanPids));
        }
        return result;
    }

    // —— 单进程判定：收集全部依据 → 冲突消解取最保守级（PRD F1 处理逻辑 5：🚫 > ⚠️ > ✅）——
    private static Classification ClassifyOne(
        ProcessSnapshot p,
        WhitelistSnapshot whitelist,
        RulePack pack,
        ClassificationContext ctx,
        bool hasGlobalFailure,
        List<SignalFailure>? failures,
        long treeBytes,
        HashSet<int> orphanPids)
    {
        var bases = new List<Basis>();
        var level = Level.Unmatched;
        var s = p.Signals;
        var wouldBeRevived = default(bool?);
        var requiresElevation = false;
        var hasRecommendBasis = false;    // ✅ 依据命中（小体量降级/旁证的适用前提）
        var exemptFromSmallTree = false;  // 口径 #13：孤儿与模式库命中豁免小体量降级
        var compromised = false;          // 保守兜底已触发：终态不得为 Recommend（system 法-3）
        var listUnavailable = false;      // 保护性名单缺失（法-3）

        // 白名单（口径 #14）：完全排除，最高优先；名称匹配，v1 已知边界（同名不同路径一并排除）
        if (whitelist.ContainsName(p.Name))
        {
            bases.Add(new Basis(14, $"白名单命中：{p.Name}"));
            return Assemble(p, Level.Whitelisted, bases, treeBytes, wouldBeRevived, s, requiresElevation);
        }

        // 采集失败兜底（system 法-3；GWT#12 PPL=🚫 与 GWT#13 提权=⚠️ 的区分载体）
        if (failures?.Any(f => f.Kind == FailureKind.AccessDenied) == true)
        {
            bases.Add(new Basis(11, "受保护进程（打开受拒，疑似 PPL）——口径 #11"));
            return Assemble(p, Level.Protected, bases, treeBytes, wouldBeRevived, s,
                requiresElevation: true);
        }
        if (failures?.Any(f => f.Kind == FailureKind.Unreadable) == true)
        {
            bases.Add(new Basis(0, "元数据不可读（保守降级，不进✅级）"));
            level = Promote(level, Level.Caution);
            compromised = true;
        }
        if (failures?.Any(f => f.Kind == FailureKind.CollectorFailed) == true || hasGlobalFailure)
        {
            bases.Add(new Basis(0, "信号采集失败（保守降级，不进✅级）"));
            level = Promote(level, Level.Caution);
            compromised = true;
        }

        // 路径不可得（契约「ExecutablePath 可空=受保护/系统」；无失败记录时按保守降级承载）
        if (p.ExecutablePath == null
            && !(failures?.Any(f => f.Kind is FailureKind.AccessDenied or FailureKind.Unreadable) ?? false))
        {
            bases.Add(new Basis(0, "进程路径不可得（保守降级，不进✅级）"));
            level = Promote(level, Level.Caution);
            compromised = true;
        }

        // 🚫 系统保护名单（PRD F1-4 穷举名；口径表无独立编号 → SignalId=0 保留值）
        if (pack.ProtectedProcesses.Count == 0)
        {
            listUnavailable = true;
        }
        else if (pack.ProtectedProcesses.Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase)))
        {
            bases.Add(new Basis(0, $"系统保护名单命中：{p.Name}"));
            level = Promote(level, Level.Protected);
        }

        // 🚫 安全软件双通道（口径 #11：名单名 + 签名方）
        if (pack.SecurityApps.Count == 0)
        {
            listUnavailable = true;
        }
        else
        {
            var hit = pack.SecurityApps.FirstOrDefault(a =>
                string.Equals(a.Name, p.Name, StringComparison.OrdinalIgnoreCase)
                || (a.Signer != null && s.SignerName != null
                    && string.Equals(a.Signer, s.SignerName, StringComparison.OrdinalIgnoreCase)));
            if (hit != null)
            {
                bases.Add(new Basis(11, $"安全软件：{hit.Name}"));
                level = Promote(level, Level.Protected);
            }
        }

        // 🚫 微软签名（口径 #9「微软 → 🚫候选」；PRD 附录三通道之一，独立于目录判定）
        if (s.SignatureStatus == SignatureStatus.Microsoft)
        {
            bases.Add(new Basis(9, "微软签名进程"));
            level = Promote(level, Level.Protected);
        }

        // 🚫 验不了（口径 #9 兜底：「Access Denied/验不了 → 按受保护处理」，独立于目录）
        if (s.SignatureStatus == SignatureStatus.Unverifiable)
        {
            bases.Add(new Basis(9, "签名验证不可行（按受保护处理）"));
            level = Promote(level, Level.Protected);
        }

        // 🚫 系统目录（口径 #9/#10：目录命中直接按微软处理，不验签，签名状态无关；
        // WindowsApps 仅用于 UWP 识别，不触发本链——v1.2 UWP 链路裁决）
        if (!s.IsUwpPackage)
        {
            if (s.IsSystemDirectory == true)
            {
                bases.Add(new Basis(10, "系统目录进程"));
                level = Promote(level, Level.Protected);
            }
            else if (s.IsSystemDirectory == null)
            {
                bases.Add(new Basis(10, "系统目录判定不可读（保守视为系统目录）"));
                level = Promote(level, Level.Protected);
            }
        }

        // 🚫 本工具自身（ClassificationContext.SelfPid 注入，纯函数无环境读取）
        if (p.Pid == ctx.SelfPid)
        {
            bases.Add(new Basis(0, "本工具自身"));
            level = Promote(level, Level.Protected);
        }

        // 服务（口径 #8；v1.2 裁决：服务类归🚫带原因，"不推荐也说明"）
        if (s.ServiceName != null)
        {
            requiresElevation = true; // 预标：服务进程（口径 #8，契约 §1.2）
            switch (s.ServiceRestartOnFailure)
            {
                case true:
                    bases.Add(new Basis(8, $"杀掉后会被服务管理器拉起（服务名：{s.ServiceName}）"));
                    level = Promote(level, Level.Caution);
                    wouldBeRevived = true;
                    break;
                case false:
                    bases.Add(new Basis(8, "服务进程——建议经 services.msc 禁用来源后重启"));
                    level = Promote(level, Level.Protected);
                    break;
                case null:
                    bases.Add(new Basis(8, "服务失败恢复配置不可读（保守降级，不进✅级）"));
                    level = Promote(level, Level.Caution);
                    compromised = true;
                    break;
            }
        }

        // 🚫 有可见窗口（口径 #4；枚举失败视为有窗口，保守）
        if (!s.IsUwpPackage)
        {
            if (s.HasVisibleWindow == true)
            {
                bases.Add(new Basis(4, "有可见窗口（正在使用）"));
                level = Promote(level, Level.Protected);
            }
            else if (s.HasVisibleWindow == null)
            {
                bases.Add(new Basis(4, "窗口枚举失败（保守视为有窗口）"));
                level = Promote(level, Level.Protected);
            }
        }

        // ⚠️ UWP 特例（口径 #4：包进程窗口挂 ApplicationFrameHost 枚举不到，保守归⚠️）
        if (s.IsUwpPackage)
        {
            bases.Add(new Basis(4, "UWP 包进程（特例保守处理）"));
            level = Promote(level, Level.Caution);
            compromised = true;
        }

        // ✅ 孤儿（口径 #1：父退出 / PID 复用）
        if (s.OrphanHint == OrphanHint.ParentDead)
        {
            bases.Add(new Basis(1, "孤儿进程（父进程已退出）"));
            hasRecommendBasis = true;
            exemptFromSmallTree = true;
        }
        else if (s.OrphanHint == OrphanHint.PidReused)
        {
            bases.Add(new Basis(1, "孤儿进程（PID 复用，真父已退出）"));
            hasRecommendBasis = true;
            exemptFromSmallTree = true;
        }

        // ✅ 残留模式库（口径 #2：进程名/路径子串，不区分大小写）
        var residual = MatchResidual(p, pack.ResidualPatterns);
        if (residual != null)
        {
            bases.Add(new Basis(2, $"残留模式命中：{residual}"));
            hasRecommendBasis = true;
            exemptFromSmallTree = true;
        }

        // ✅ 无窗口用户级应用（F1 处理逻辑 2d）
        if (!s.IsUwpPackage
            && s.HasVisibleWindow == false
            && s.ServiceName == null
            && s.IsSystemDirectory == false)
        {
            bases.Add(new Basis(4, "无窗口用户级应用"));
            hasRecommendBasis = true;
        }

        // ✅ 授予：依据命中且未被兜底击穿（Promote 单调性保证 compromised 终态不可为 Recommend）
        if (hasRecommendBasis && !compromised)
        {
            level = Promote(level, Level.Recommend);
        }

        // 旁证降级（口径 #3）：✅ 候选同目录存在其他存活进程，剔除孤儿命中者与自身 → 降⚠️
        if (hasRecommendBasis && !compromised && level == Level.Recommend && s.SameDirAlivePids.Count > 0)
        {
            var witness = s.SameDirAlivePids.Where(pid => pid != p.Pid && !orphanPids.Contains(pid)).ToList();
            if (witness.Count > 0)
            {
                bases.Add(new Basis(3, "同目录存在存活进程，疑似在用组件"));
                level = Promote(level, Level.Caution);
            }
        }

        // ⚠️ 常驻应用（口径 #5：精确名）
        if (pack.ResidentApps.Count == 0)
        {
            listUnavailable = true;
        }
        else if (pack.ResidentApps.Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase)))
        {
            bases.Add(new Basis(5, "疑似常驻应用"));
            level = Promote(level, Level.Caution);
        }

        // ⚠️ 活跃网络连接（口径 #6：仅 ESTABLISHED 计活跃）
        if (s.TcpEstablishedCount > 0)
        {
            bases.Add(new Basis(6, $"有活跃网络连接（{s.TcpEstablishedCount} 条 ESTABLISHED）"));
            level = Promote(level, Level.Caution);
        }

        // ⚠️/兜底 CPU 活动（口径 #7：差分 >1s；不可读 → 不进✅级）
        if (s.CpuDeltaSeconds > CpuDeltaThresholdSeconds)
        {
            bases.Add(new Basis(7, "疑似任务进行中（CPU 差分显著）"));
            level = Promote(level, Level.Caution);
        }
        else if (s.CpuDeltaSeconds == null)
        {
            bases.Add(new Basis(7, "CPU 差分不可读（保守降级，不进✅级）"));
            level = Promote(level, Level.Caution);
            compromised = true;
        }

        // ⚠️ 计划任务拉起（口径 #15）；有任务来源但匹配不可读 → 保守降级
        if (s.ScheduledTaskWouldRevive == true)
        {
            bases.Add(new Basis(15, "存在对应计划任务，杀后会被拉起"));
            level = Promote(level, Level.Caution);
            wouldBeRevived = true;
        }
        else if (s.ScheduledTaskWouldRevive == null
            && s.SourceEntries.Any(e => e.Type == SourceType.ScheduledTask))
        {
            bases.Add(new Basis(15, "计划任务匹配不可读（保守降级，不进✅级）"));
            level = Promote(level, Level.Caution);
            compromised = true;
        }

        // 小体量降级（口径 #13）：非豁免✅候选且树合计 <50MB → 降⚠️
        // 内存不可读的语义由 SignalFailure（Unreadable）承载并先置 compromised，不进入本块
        if (hasRecommendBasis && !compromised && !exemptFromSmallTree && level == Level.Recommend
            && treeBytes < SmallTreeThresholdBytes)
        {
            bases.Add(new Basis(13, $"占用过小（树合计 {treeBytes / 1024 / 1024} MB < 50 MB）"));
            level = Promote(level, Level.Caution);
        }

        // 法-3：保护性名单缺失（编排方传入空 RulePack）→ 不进✅级
        if (listUnavailable && level == Level.Recommend)
        {
            bases.Add(new Basis(0, "保护名单不可用（保守降级，不进✅级）"));
            level = Level.Caution;
        }

        // 终局验签闸门（口径 #9 单通道 SPOF 防御）：验签通道缺失/失败未伴随 SignalFailure 时，
        // 未验签（NotCollected）的✅候选降⚠️——候选验签本应只对✅/⚠️候选执行（编排契约）
        if (level == Level.Recommend && s.SignatureStatus == SignatureStatus.NotCollected)
        {
            bases.Add(new Basis(9, "验签未执行（保守降级，不进✅级）"));
            level = Level.Caution;
        }

        return Assemble(p, level, bases, treeBytes, wouldBeRevived, s, requiresElevation);
    }

    private static Classification Assemble(
        ProcessSnapshot p, Level level, List<Basis> bases, long treeBytes,
        bool? wouldBeRevived, SignalSet s, bool requiresElevation) =>
        new(p.Pid, level, bases, treeBytes, wouldBeRevived, s.SourceEntries, requiresElevation);

    /// <summary>冲突消解：多级命中取最保守（Protected > Caution > Recommend；Unmatched 为起点）。</summary>
    private static Level Promote(Level current, Level candidate) =>
        Severity(candidate) > Severity(current) ? candidate : current;

    private static int Severity(Level l) => l switch
    {
        Level.Protected => 3,
        Level.Caution => 2,
        Level.Recommend => 1,
        _ => 0,
    };

    private static string? MatchResidual(ProcessSnapshot p, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (p.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || (p.ExecutablePath?.Contains(pattern, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                return pattern;
            }
        }
        return null;
    }

    /// <summary>口径 #1 全集：孤儿判定命中者（旁证互斥用：孤儿群互不为旁证）。</summary>
    private static HashSet<int> CollectOrphanPids(IReadOnlyList<ProcessSnapshot> snapshots) =>
        snapshots.Where(p => p.Signals.OrphanHint != OrphanHint.No)
            .Select(p => p.Pid)
            .ToHashSet();

    /// <summary>
    /// 口径 #13：树合计私有提交（树边界 = 该进程的全部后代进程）。
    /// 防环占位保证终止性；真实快照 PPID 不成环（scanner 契约），环属违约输入，
    /// 此时环内合计为近似值（起点节点计入两次）——不为违约数据追求数学精确。
    /// </summary>
    private static Dictionary<int, long> ComputeTreePrivateBytes(IReadOnlyList<ProcessSnapshot> snapshots)
    {
        var byPid = snapshots.ToDictionary(p => p.Pid); // Pid 唯一是 scanner 契约（data-contracts §1.1）
        var children = snapshots.GroupBy(p => p.ParentPid)
            .ToDictionary(g => g.Key, g => g.ToList());
        var memo = new Dictionary<int, long>(snapshots.Count);

        long Subtree(int pid)
        {
            if (memo.TryGetValue(pid, out var known))
            {
                return known;
            }
            if (!byPid.TryGetValue(pid, out var self))
            {
                return 0; // 父在快照外：贡献 0
            }
            memo[pid] = self.PrivateCommittedBytes; // 防环占位
            long sum = self.PrivateCommittedBytes;
            if (children.TryGetValue(pid, out var kids))
            {
                foreach (var kid in kids)
                {
                    sum += Subtree(kid.Pid);
                }
            }
            memo[pid] = sum;
            return sum;
        }

        foreach (var p in snapshots)
        {
            Subtree(p.Pid);
        }
        return memo;
    }
}
