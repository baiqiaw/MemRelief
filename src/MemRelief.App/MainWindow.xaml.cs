using System.Windows;
using MemRelief.App.ViewModels;
using MemRelief.Core;

namespace MemRelief.App;

/// <summary>
/// 主窗口（View 层）：仅绑定，无业务逻辑（ui.md 职责边界）。
/// 布局：顶部概览条 + 搜索框 + 三级列表（T-15）+ 底部操作栏；释放交互与白名单管理面板由 T-16/T-26 落地。
/// 概览条（T-17）为独立子 VM（并行车道隔离），经可选参数注入；未注入时控件自折叠——
/// 组合根注入行属并行接线缺口，归编排器收口（见 T-17 报告）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel, OverviewBarViewModel? overviewBar = null)
    {
        InitializeComponent();
        Title = ProductInfo.Name; // 标题取产品元数据单一来源（Core ProductInfo）
        _viewModel = viewModel;
        DataContext = viewModel;
        if (overviewBar is not null)
        {
            // 子 VM 数据面独立于 MainViewModel；状态机实例须与主 VM 同源（组合根接线时保证）
            OverviewBarHost.DataContext = overviewBar;
        }
        // 启动时点概览刷新（PRD F5 三时点之一）；内部已收口采样失败，幂等只读，重复 Loaded 无副作用
        Loaded += (_, _) => _ = _viewModel.InitializeAsync();
    }
}
