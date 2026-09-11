using System.Windows;
using MemRelief.App.ViewModels;
using MemRelief.Core;

namespace MemRelief.App;

/// <summary>
/// 主窗口（View 层）：仅绑定，无业务逻辑（ui.md 职责边界）。
/// 布局骨架：顶部概览条 + 搜索框 + 中部列表区 + 底部操作栏；三级列表/释放/白名单面板由 T-15/T-16/T-26 落地。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        Title = ProductInfo.Name; // 标题取产品元数据单一来源（Core ProductInfo）
        _viewModel = viewModel;
        DataContext = viewModel;
        // 启动时点概览刷新（PRD F5 三时点之一）；内部已收口采样失败，幂等只读，重复 Loaded 无副作用
        Loaded += (_, _) => _ = _viewModel.InitializeAsync();
    }
}
