using System.Windows.Input;

namespace MemRelief.App.ViewModels;

/// <summary>
/// 薄命令封装（App 内自建，不引命令框架）。可用性刷新双通道：
/// ①CanExecuteChanged 挂 WPF CommandManager.RequerySuggested（焦点/输入变化时重查）；
/// ②状态机矩阵变化时 VM 对命令属性发 PropertyChanged（RaiseAll），绑定重取命令值即重查 CanExecute——
/// 后者保证矩阵变化即时投影到按钮，不依赖用户下一次输入。
/// </summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <summary>实时求值（不缓存），调用方语义=“当前矩阵是否允许此动作”。</summary>
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();
}

/// <summary>带参薄命令封装（右键加白等行级操作；CanExecute 按参数与矩阵实时求值）。</summary>
public sealed class RelayCommand<T>(Action<T?> execute, Func<T?, bool>? canExecute = null) : ICommand
    where T : class
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) =>
        canExecute?.Invoke(parameter as T) ?? true;

    public void Execute(object? parameter)
    {
        if (parameter is T typed)
        {
            execute(typed);
        }
    }
}
