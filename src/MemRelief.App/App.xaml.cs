using System.Windows;
using MemRelief.App.Hosting;

namespace MemRelief.App;

/// <summary>应用入口：组合根构建对象图并展示主窗口（StartupUri 移除，构造经 CompositionRoot）。</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 全局兜底：未处理异常收口为可读提示，不向用户裸抛堆栈、不静默退出（失败可观测）
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"发生未处理错误：{args.Exception.Message}",
                "MemRelief", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        MainWindow = CompositionRoot.CreateMainWindow();
        MainWindow.Show();
    }
}
