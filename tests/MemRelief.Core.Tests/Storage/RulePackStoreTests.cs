using MemRelief.Core.Contracts;
using MemRelief.Core.Storage;
using Xunit;

namespace MemRelief.Core.Tests.Storage;

// 名单资源装载单测（T-13）：四份基线与 PRD 附录一致、失败上报 fail-safe（storage 法）、空数组合法、防御性清洗
public class RulePackStoreTests
{
    private static RulePackLoadResult Load(Func<string, string?> loader) => new RulePackStore(loader).LoadRulePack();

    // —— CASE：四份基线装载（AC：与 PRD 附录名单清单一致）——

    [Fact]
    public void 四份基线装载_内容与PRD附录清单一致()
    {
        var result = new RulePackStore().LoadRulePack();

        Assert.Empty(result.Failures);
        // PRD 附录 #1：残留模式库（不区分大小写子串）
        Assert.Equal(
            new[] { "updater", "update.exe", "crashpad", "crashreporter", "setup" },
            result.Pack.ResidualPatterns);
        // PRD 附录 #2：常驻应用（精确名；网盘/下载器待用户按本机补齐，M2 前）
        Assert.Equal(new[] { "WeChat.exe", "Weixin.exe" }, result.Pack.ResidentApps);
        // PRD 附录 #3：安全软件（Defender 首版；其余待用户按本机补齐，M2 前）
        var security = Assert.Single(result.Pack.SecurityApps);
        Assert.Equal("MsMpEng.exe", security.Name);
        Assert.Null(security.Signer);
        // PRD 附录 #4：系统保护穷举名（9 项全在 PRD 明文）
        Assert.Equal(
            new[] { "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
                    "lsass.exe", "svchost.exe", "dwm.exe", "lsaiso.exe" },
            result.Pack.ProtectedProcesses);
    }

    [Fact]
    public void 重复装载_结果一致()
    {
        var a = new RulePackStore().LoadRulePack();
        var b = new RulePackStore().LoadRulePack();
        Assert.Equal(a.Pack.ResidualPatterns, b.Pack.ResidualPatterns);
        Assert.Equal(a.Pack.ResidentApps, b.Pack.ResidentApps);
        Assert.Equal(a.Pack.SecurityApps, b.Pack.SecurityApps);
        Assert.Equal(a.Pack.ProtectedProcesses, b.Pack.ProtectedProcesses);
        Assert.Equal(a.Failures, b.Failures);
    }

    // —— CASE：失败上报 fail-safe（AC：加载失败→上报→编排方使 rules 保守兜底，禁空名单放行）——

    [Fact]
    public void 保护类名单缺失_上报失败_该名单置空_其余正常()
    {
        var result = Load(name => name switch
        {
            "security-apps" => null,                    // 缺失
            "resident-apps" => """["WeChat.exe"]""",    // 其余名单正常装载
            _ => "[]",
        });

        var failure = Assert.Single(result.Failures);
        Assert.Equal(RulePackList.SecurityApps, failure.List);
        Assert.Contains("缺失", failure.Reason);
        Assert.Empty(result.Pack.SecurityApps);
        Assert.Equal(new[] { "WeChat.exe" }, result.Pack.ResidentApps);
    }

    [Fact]
    public void 保护类名单JSON损坏_上报失败()
    {
        var result = Load(name => name == "protected-processes" ? "{ not valid json" : "[]");

        var failure = Assert.Single(result.Failures);
        Assert.Equal(RulePackList.ProtectedProcesses, failure.List);
        Assert.Contains("解析", failure.Reason);
        Assert.Empty(result.Pack.ProtectedProcesses);
    }

    [Fact]
    public void 非保护类名单损坏_同样上报_不影响其余名单()
    {
        var result = Load(name => name == "residual-patterns" ? "not-json" : "[]");

        var failure = Assert.Single(result.Failures);
        Assert.Equal(RulePackList.ResidualPatterns, failure.List);
        Assert.Empty(result.Pack.ResidualPatterns);
    }

    [Fact]
    public void 资源读取抛异常_逐名单上报读取失败()
    {
        var result = Load(_ => throw new IOException("模拟资源读取故障"));

        Assert.Equal(4, result.Failures.Count);
        Assert.All(result.Failures, f => Assert.Contains("读取失败", f.Reason));
        Assert.Empty(result.Pack.ResidualPatterns);
        Assert.Empty(result.Pack.ResidentApps);
        Assert.Empty(result.Pack.SecurityApps);
        Assert.Empty(result.Pack.ProtectedProcesses);
    }

    [Fact]
    public void 名单条目带首尾空格_装载时清洗_精确名匹配不漏配()
    {
        var result = Load(name => name == "security-apps"
            ? """[{"name":" MsMpEng.exe "}]"""
            : "[]");

        Assert.Empty(result.Failures);
        Assert.Equal("MsMpEng.exe", Assert.Single(result.Pack.SecurityApps).Name);
    }

    // —— CASE：空数组与容错（契约：v1 不区分真实空与失败，保守误降级由 rules 侧承担）——

    [Fact]
    public void 空数组_合法装载_不报失败()
    {
        var result = Load(_ => "[]");

        Assert.Empty(result.Failures);
        Assert.Empty(result.Pack.ResidualPatterns);
        Assert.Empty(result.Pack.ResidentApps);
        Assert.Empty(result.Pack.SecurityApps);
        Assert.Empty(result.Pack.ProtectedProcesses);
    }

    [Fact]
    public void Signer缺失_解析为null_仅名称通道匹配语义不变()
    {
        var result = Load(name => name == "security-apps"
            ? """[{"name":"TestAv.exe"},{"name":"OtherAv.exe","signer":"Other Signer Co."}]"""
            : "[]");

        Assert.Empty(result.Failures);
        Assert.Null(result.Pack.SecurityApps[0].Signer);
        Assert.Equal("Other Signer Co.", result.Pack.SecurityApps[1].Signer);
    }

    [Fact]
    public void 条目含注释与尾逗号_宽容解析_空白条目剔除()
    {
        var result = Load(name => name == "resident-apps"
            ? """
              [
                // 托盘常驻，用户手工维护
                "WeChat.exe",
                "  ",
                "Weixin.exe",
              ]
              """
            : "[]");

        Assert.Empty(result.Failures);
        Assert.Equal(new[] { "WeChat.exe", "Weixin.exe" }, result.Pack.ResidentApps);
    }
}
