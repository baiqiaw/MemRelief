using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;
using Xunit;

namespace MemRelief.Core.Tests.Scanner;

// 内存概览采样单测（T-05）：三通道纯函数换算的不变量 + 降级语义（AC：standby 不可得→Standby=null 且 Source=Degraded）
// 真机通道可用性与"同分钟对照 ≤10%"（R05 GWT）由 ScannerIntegrationTests 冒烟 + T-22 真机验收承载
public class MemoryOverviewTests
{
    private const int Page4K = 4096;

    // —— CASE：NtQuery 通道（口径换算 + 降级）——

    [Fact]
    public void NtQuery全量_四数值换算_standby按优先级求和()
    {
        var overview = MemoryOverviewSampler.FromNtQuery(
            physicalTotalBytes: 16L * 1024 * 1024 * 1024,
            availablePages: 1_000_000,
            committedPages: 2_000_000,
            commitLimitPages: 4_000_000,
            pageSize: Page4K,
            standbyByPriority: new ulong[] { 10, 20, 30, 40, 50, 60, 70, 80 });

        Assert.Equal(MemoryOverviewSource.NtQuery, overview.Source);
        Assert.Equal(16L * 1024 * 1024 * 1024, overview.PhysicalTotalBytes);
        Assert.Equal(16L * 1024 * 1024 * 1024 - 1_000_000L * Page4K, overview.InUseBytes);
        Assert.Equal(2_000_000L * Page4K, overview.CommitBytes);
        Assert.Equal(4_000_000L * Page4K, overview.CommitLimitBytes);
        Assert.Equal(360L * Page4K, overview.StandbyBytes);
    }

    [Fact]
    public void NtQuery缺内存清单_核心四值保留_standby降级()
    {
        var overview = MemoryOverviewSampler.FromNtQuery(
            16L * 1024 * 1024 * 1024, 1_000_000, 2_000_000, 4_000_000, Page4K,
            standbyByPriority: null);

        Assert.Equal(MemoryOverviewSource.Degraded, overview.Source);
        Assert.Null(overview.StandbyBytes);
        Assert.True(overview.CommitBytes > 0);
        Assert.True(overview.CommitLimitBytes > 0);
    }

    [Fact]
    public void NtQuery清单全零_等同不可得_降级()
    {
        var overview = MemoryOverviewSampler.FromNtQuery(
            16L * 1024 * 1024 * 1024, 1_000_000, 2_000_000, 4_000_000, Page4K,
            standbyByPriority: new ulong[8]);

        Assert.Equal(MemoryOverviewSource.Degraded, overview.Source);
        Assert.Null(overview.StandbyBytes);
    }

    // —— CASE：PDH 通道（double 字节值 + standby 三计数器可选）——

    [Fact]
    public void Pdh含standby_来源标Pdh()
    {
        var overview = MemoryOverviewSampler.FromPdh(
            committedBytes: 8e9, commitLimitBytes: 16e9, availableBytes: 4e9,
            standbyBytes: 3e9,
            physicalTotalBytes: 16L * 1024 * 1024 * 1024);

        Assert.Equal(MemoryOverviewSource.Pdh, overview.Source);
        Assert.Equal(3_000_000_000L, overview.StandbyBytes);
        Assert.Equal(8_000_000_000L, overview.CommitBytes);
        // 16×1024³ − 4×10⁹ = 13,179,869,184
        Assert.Equal(13_179_869_184L, overview.InUseBytes);
    }

    [Fact]
    public void Pdh缺standby_降级()
    {
        var overview = MemoryOverviewSampler.FromPdh(
            8e9, 16e9, 4e9, standbyBytes: null,
            16L * 1024 * 1024 * 1024);

        Assert.Equal(MemoryOverviewSource.Degraded, overview.Source);
        Assert.Null(overview.StandbyBytes);
    }

    // —— CASE：最终兜底（GlobalMemoryStatusEx 页面文件口径近似）——

    [Fact]
    public void 全局内存兜底_恒降级_standby为null()
    {
        var overview = MemoryOverviewSampler.FromGlobalMemory(
            totalPhysBytes: 16L * 1024 * 1024 * 1024,
            availPhysBytes: 4L * 1024 * 1024 * 1024,
            totalPageFileBytes: 20L * 1024 * 1024 * 1024,
            availPageFileBytes: 12L * 1024 * 1024 * 1024);

        Assert.Equal(MemoryOverviewSource.Degraded, overview.Source);
        Assert.Null(overview.StandbyBytes);
        Assert.Equal(16L * 1024 * 1024 * 1024 - 4L * 1024 * 1024 * 1024, overview.InUseBytes);
        Assert.Equal(20L * 1024 * 1024 * 1024 - 12L * 1024 * 1024 * 1024, overview.CommitBytes);
        Assert.Equal(20L * 1024 * 1024 * 1024, overview.CommitLimitBytes);
    }

    // —— CASE：对抗性输入（计数器毛刺不产生负值外泄）——

    [Fact]
    public void 可用内存大于物理总量_毛刺输入_InUse钳零不外泄负值()
    {
        var overview = MemoryOverviewSampler.FromPdh(
            8e9, 16e9, availableBytes: 32e9, standbyBytes: null,
            16L * 1024 * 1024 * 1024);

        Assert.Equal(0, overview.InUseBytes);
    }

    [Fact]
    public void 计数器毛刺超long语义_double转long钳顶不产生负值()
    {
        var overview = MemoryOverviewSampler.FromPdh(
            committedBytes: 1e19, commitLimitBytes: 1e19, availableBytes: 4e9, standbyBytes: 1e19,
            physicalTotalBytes: 16L * 1024 * 1024 * 1024);

        Assert.Equal(long.MaxValue, overview.CommitBytes);
        Assert.Equal(long.MaxValue, overview.CommitLimitBytes);
        Assert.Equal(long.MaxValue, overview.StandbyBytes);
        Assert.True(overview.CommitBytes > 0, "钳顶后不得外泄负值");
    }

    [Fact]
    public void 降级语义不变式_三通道统一_Standby为null当且仅当Source为Degraded()
    {
        // 三通道各构造一次正常/降级样本，锁定 AC#2 不变式（Degraded ⇔ StandbyBytes==null）
        var ntqOk = MemoryOverviewSampler.FromNtQuery(16, 1, 1, 1, Page4K, new ulong[] { 1, 0, 0, 0, 0, 0, 0, 0 });
        var ntqDg = MemoryOverviewSampler.FromNtQuery(16, 1, 1, 1, Page4K, null);
        var pdhOk = MemoryOverviewSampler.FromPdh(1, 1, 1, 1, 16);
        var pdhDg = MemoryOverviewSampler.FromPdh(1, 1, 1, null, 16);
        var gmiDg = MemoryOverviewSampler.FromGlobalMemory(16, 1, 1, 1);

        Assert.Equal(MemoryOverviewSource.NtQuery, ntqOk.Source);
        Assert.Equal(MemoryOverviewSource.Pdh, pdhOk.Source);
        foreach (var sample in new[] { ntqOk, pdhOk })
        {
            Assert.True(sample.StandbyBytes is not null || sample.Source == MemoryOverviewSource.Degraded,
                "非降级样本须携带 standby 值");
        }
        foreach (var sample in new[] { ntqDg, pdhDg, gmiDg })
        {
            Assert.Equal(MemoryOverviewSource.Degraded, sample.Source);
            Assert.Null(sample.StandbyBytes);
        }
    }
}
