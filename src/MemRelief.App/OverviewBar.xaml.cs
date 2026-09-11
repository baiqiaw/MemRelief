using System.Windows;
using System.Windows.Controls;
using MemRelief.App.ViewModels;

namespace MemRelief.App;

/// <summary>
/// 内存概览条控件（View 层，仅生命周期接线，无业务逻辑）：
/// Loaded → 驱动子 VM 启动时点刷新（PRD F5）；DataContext 未注入子 VM（编排器接线收口前）自折叠不占位；
/// Unloaded → 释放子 VM 状态机订阅（订阅随窗口生命周期释放，ui.md §6 法条）。
/// </summary>
public partial class OverviewBar : UserControl
{
    public OverviewBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => (DataContext as OverviewBarViewModel)?.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is OverviewBarViewModel viewModel)
        {
            _ = viewModel.InitializeAsync(); // 启动时点；采样失败已收口为“—”态，幂等只读
        }
        else
        {
            // 组合根尚未注入 OverviewBarViewModel（并行车道接线缺口，T-17 报告列管）：
            // 空绑定面不展示，避免误导（骨架“—”行随本控件替换一并移除）
            Visibility = Visibility.Collapsed;
        }
    }
}
