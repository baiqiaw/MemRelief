using System.Windows;
using MemRelief.App.Text;

namespace MemRelief.App.Releasing;

/// <summary>
/// 释放确认弹窗抽象（可测试缝，T-16）：确认=true 进入释放；取消=false 停留已展示态
/// （弹窗取消不产生状态触发，PRD §3.6）。生产实现为 WPF MessageBox 模态适配器
/// （仅弹窗接线无判定逻辑，覆盖率 Exclude 与 code-behind 同口径）。
/// </summary>
public interface IReleaseConfirmDialog
{
    /// <summary>展示“将结束 N 个进程树/约 X MB”，返回用户是否确认。</summary>
    bool Confirm(int treeCount, long totalPrivateBytes);
}

/// <summary>生产实现（WPF MessageBox，模态确认；内容文案=PRD F3-1“将结束 N 个进程树/约 X MB”）。</summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification =
    "WPF 模态弹窗适配器，仅 MessageBox 接线无判定逻辑（与 code-behind Exclude 同口径）")]
public sealed class MessageBoxReleaseConfirmDialog : IReleaseConfirmDialog
{
    public bool Confirm(int treeCount, long totalPrivateBytes) =>
        MessageBox.Show(
            $"将结束 {treeCount} 个进程树，合计约 {DisplayText.TreeMb(totalPrivateBytes)}。\n\n确认释放？",
            "确认释放", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
}
