using MemRelief.App.Hosting;
using MemRelief.App.Releasing;
using MemRelief.Core.Contracts;
using MemRelief.Core.Storage;

namespace MemRelief.App.Tests.TestDoubles;

/// <summary>释放日志存储假实现：记录 Append 调用与报告引用，结果可定制（T-16 日志编排单路径测试）。</summary>
internal sealed class FakeReleaseLogStore : IReleaseLogStore
{
    public int AppendCalls { get; private set; }

    public List<ReleaseReport> AppendedReports { get; } = [];

    /// <summary>Append 返回结果（默认成功；可定制为失败以驱动“本次结果未留痕”）。</summary>
    public ReleaseLogAppendResult NextResult { get; set; } = new(Persisted: true);

    public string LogFilePath => @"C:\fake\releases.jsonl";

    public ReleaseLogAppendResult Append(ReleaseReport report)
    {
        AppendCalls++;
        AppendedReports.Add(report);
        return NextResult;
    }

    public ReleaseLogReadResult ReadAll() => new([], 0);
}

/// <summary>释放确认弹窗假实现（T-16）：Confirm 决定确认/取消路径，记录弹窗内容（N 树/X MB）。</summary>
internal sealed class FakeConfirmDialog : IReleaseConfirmDialog
{
    public int ShowCalls { get; private set; }

    public (int TreeCount, long TotalBytes) LastShown { get; private set; }

    /// <summary>弹窗应答（true=确认释放；false=取消停留已展示态）。</summary>
    public bool Reply { get; set; } = true;

    public bool Confirm(int treeCount, long totalPrivateBytes)
    {
        ShowCalls++;
        LastShown = (treeCount, totalPrivateBytes);
        return Reply;
    }
}

/// <summary>应用重启器假实现（T-16 提权重启编排）：记录重启请求，可注入应答（成功/UAC 拒绝）。</summary>
internal sealed class FakeRestarter : IAppRestarter
{
    public int RestartCalls { get; private set; }

    public IReadOnlyList<RestartFailedItem>? LastFailedItems { get; private set; }

    /// <summary>Restart 应答（null=成功；非 null=抛出该异常模拟 UAC 拒绝等失败）。</summary>
    public Exception? OnRestart { get; set; }

    public void Restart(IReadOnlyList<RestartFailedItem> failedItems)
    {
        RestartCalls++;
        LastFailedItems = failedItems;
        if (OnRestart is not null)
        {
            throw OnRestart;
        }
    }
}

/// <summary>应用关闭假实现（T-16 重启成功后关窗动作计数）。</summary>
internal sealed class FakeShutdown
{
    public int Calls { get; private set; }

    public Action AsAction() => () => Calls++;
}
