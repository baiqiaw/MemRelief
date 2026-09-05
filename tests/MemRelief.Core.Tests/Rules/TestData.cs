using MemRelief.Core.Contracts;

namespace MemRelief.Core.Tests.Rules;

// 合成数据构造：对齐 PRD §3.2 GWT「前置构造规格」——无窗口、无连接、非服务、非 UWP、
// 非常驻、非系统目录、同目录无存活、树合计 >50MB 为"干净用户级应用"默认
internal static class Snap
{
    public static readonly DateTime T = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    public const long Mb = 1024 * 1024;

    public static ProcessSnapshot Clean(
        int pid,
        int ppid = 0,
        string name = "app.exe",
        long bytes = 60 * Mb,
        string? path = null,
        SignalSet? signals = null) =>
        new(pid, ppid, name, path ?? @"C:\apps\" + name, T.AddMinutes(-pid), bytes,
            Signals: signals ?? CleanSignals());

    public static SignalSet CleanSignals() => new(
        HasVisibleWindow: false,
        CpuDeltaSeconds: 0.0, // 已采样、无活动（null = 读取失败，会触发保守降级）
        IsSystemDirectory: false,
        SignatureStatus: SignatureStatus.ValidNonMicrosoft);

    public static SignalSet Orphan(OrphanHint hint = OrphanHint.ParentDead) => CleanSignals() with
    {
        OrphanHint = hint,
    };

    public static ScanResult Scan(params ProcessSnapshot[] snapshots) =>
        ScanWith(snapshots);

    public static ScanResult ScanWith(IEnumerable<ProcessSnapshot> snapshots, params SignalFailure[] failures) =>
        new(T, snapshots.Count(), 500, snapshots.ToArray(), failures);

    public static WhitelistSnapshot Whitelist(params string[] names) =>
        new(names.Select(n => new WhitelistEntry(n, T)).ToArray());

    public static RulePack Pack() => new(
        ResidualPatterns: new[] { "crashpad", "updater", "update.exe", "crashreporter", "setup" },
        ResidentApps: new[] { "WeChat.exe", "OneDrive.exe" },
        SecurityApps: new[] { new SecurityApp("360Tray.exe", "360"), new SecurityApp("QQPCTray.exe", "Tencent") },
        ProtectedProcesses: new[] { "wininit.exe", "csrss.exe", "lsass.exe", "svchost.exe", "services.exe", "dwm.exe" });

    public static ClassificationContext Ctx(int selfPid = 9999) => new(selfPid, "tester");
}
