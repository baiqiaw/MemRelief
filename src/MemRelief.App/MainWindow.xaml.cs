using System.ComponentModel;
using System.Windows;
using MemRelief.App.ViewModels;
using MemRelief.Core;

namespace MemRelief.App;

/// <summary>
/// 主窗口（View 层）：仅绑定与生命周期接线，无业务逻辑（ui.md 职责边界）。
/// 布局：顶部概览条 + 状态行 + 报告面板 + 搜索框 + 三级列表 + 底部操作栏。
/// 关窗语义（PRD §3.6 注）：扫描中关窗=直接退出丢弃部分结果（默认关闭行为）；
/// 释放中关窗=取消未开始树并等待 ReleaseCompleted 后方可退出（本类只做 e.Cancel 与再关，语义归 VM）。
/// 概览条（T-17）自驱动启动时点刷新（控件 Loaded）；主 VM 概览链已随 #38 步骤 2 删除，无 Loaded 接线。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closeAfterReleasePending;

    public MainWindow(MainViewModel viewModel, OverviewBarViewModel? overviewBar = null)
    {
        InitializeComponent();
        Title = ProductInfo.Name; // 标题取产品元数据单一来源（Core ProductInfo；单实例激活按标题定位）
        _viewModel = viewModel;
        DataContext = viewModel;
        if (overviewBar is not null)
        {
            // 子 VM 数据面独立于 MainViewModel；状态机实例须与主 VM 同源（组合根接线时保证）
            OverviewBarHost.DataContext = overviewBar;
        }

        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.PrepareClose())
        {
            return; // 非释放中：直接关（扫描中关窗=丢弃部分结果，PRD §3.6）
        }

        // 释放中：本次关闭取消，等取消收尾（ReleaseCompleted+日志落盘）后再关
        e.Cancel = true;
        if (_closeAfterReleasePending)
        {
            return; // 收尾等待已在途（用户再次点关）：不重复启动等待链
        }

        _closeAfterReleasePending = true;
        _ = CloseAfterReleaseAsync();
    }

    private async Task CloseAfterReleaseAsync()
    {
        await _viewModel.SettleReleaseAsync().ConfigureAwait(true);
        _closeAfterReleasePending = false;
        Close(); // 收尾完成后再关；Close 幂等（已关窗口二次调用无操作）
    }
}
