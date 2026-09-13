using System.Windows;
using MemRelief.App.Hosting;
using MemRelief.App.ViewModels;
using MemRelief.Core;

namespace MemRelief.App;

/// <summary>
/// 应用入口：单实例互斥（T-27）→ 重启参数解析（T-27）→ 组合根构建对象图并展示主窗口（StartupUri 移除）。
/// 启动行为（data-contracts §2 ③.s4 裁决⑧）：正常启动不自动扫描（用户点击触发）；
/// 仅携带重启参数（提权重启链路）时自动扫描。互斥非主实例（普通二次启动/重启等待超时）：
/// 激活既有窗口后退出（裁决⑨，防静默双实例）。
/// </summary>
public partial class App : Application
{
    private SingleInstanceGuard? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var restart = RestartOptions.Parse(e.Args);
        _singleInstance = SingleInstanceGuard.Acquire(waitForExisting: restart.AutoRescan);
        if (!_singleInstance.IsPrimary)
        {
            // 已有实例：按标题定位并前置（旧实例仍在——普通二次启动或重启等待超时），本实例退出
            ExistingWindowActivator.Activate(ProductInfo.Name);
            Shutdown();
            return;
        }

        // 全局兜底：未处理异常收口为可读提示，不向用户裸抛堆栈、不静默退出（失败可观测）
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"发生未处理错误：{args.Exception.Message}",
                "MemRelief", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        try
        {
            MainWindow = CompositionRoot.CreateMainWindow(restart);
            MainWindow.Show();
            if (restart.AutoRescan)
            {
                // 管理员重启链路：系统触发自动扫描（PRD §3.6 未扫描态离开条件“以管理员身份重启后启动”）
                if (MainWindow.DataContext is MainViewModel viewModel)
                {
                    _ = viewModel.StartScanAsync();
                }
            }
        }
        catch (Exception ex)
        {
            // fail-closed（cross-review 收口）：启动段失败不得 Handled=true 后残留无窗僵尸进程——
            // 该进程持有单实例互斥，后续所有启动（含提权重启）都会被判“已有实例”而自锁
            MessageBox.Show(
                $"启动失败：{ex.Message}", "MemRelief", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose(); // 互斥让位（重启链路新实例的有限等待随即夺锁）
        base.OnExit(e);
    }
}
