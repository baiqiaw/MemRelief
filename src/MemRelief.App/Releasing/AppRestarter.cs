using System.Diagnostics;
using MemRelief.App.Hosting;

namespace MemRelief.App.Releasing;

/// <summary>
/// 应用重启器抽象（T-16 提权重启编排）：提权拉起新实例并携带重启参数（自动重扫+失败项清单）。
/// </summary>
public interface IAppRestarter
{
    /// <summary>
    /// 以管理员身份重启（runAs+重启参数；自动重扫恒开）。失败经异常上抛由调用方收口：
    /// UAC 拒绝=Win32Exception 1223（停留普通权限）；32K 超限降级由 <see cref="RestartOptions.Create"/> 承载。
    /// </summary>
    void Restart(IReadOnlyList<RestartFailedItem> failedItems);
}

/// <summary>
/// 生产实现：Process.Start(runAs) 拉起自身可执行文件。
/// 参数组装（32K 收口+失败项序列化）在此单点：与 <see cref="RestartOptions"/> round-trip 测试共同钉住
/// 产生侧↔解析侧一致性。processStarter 可注入（测试捕获 ProcessStartInfo，不真启进程）。
/// </summary>
public sealed class ElevationRestarter : IAppRestarter
{
    private readonly string _executablePath;
    private readonly Func<ProcessStartInfo, Process?> _processStarter;

    /// <summary>executablePath 缺省取当前进程主模块路径（单文件发布/普通形态均有效）。</summary>
    public ElevationRestarter(
        string? executablePath = null,
        Func<ProcessStartInfo, Process?>? processStarter = null)
    {
        _executablePath = executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("无法定位可执行文件路径，提权重启不可用");
        _processStarter = processStarter ?? (psi => Process.Start(psi));
    }

    public void Restart(IReadOnlyList<RestartFailedItem> failedItems)
    {
        // 32K 上限收口（data-contracts §2 ③.s4 裁决①：超限降级“不携带、仅自动重扫”，T-16 产生侧）
        var options = RestartOptions.Create(autoRescan: true, failedItems, _executablePath);
        _processStarter(new ProcessStartInfo
        {
            FileName = _executablePath,
            Arguments = string.Join(" ", options.ToArguments()),
            UseShellExecute = true,
            Verb = "runAs",
        });
    }
}
