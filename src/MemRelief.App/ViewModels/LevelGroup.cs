using System.ComponentModel;
using MemRelief.App.Text;
using MemRelief.Core.Contracts;

namespace MemRelief.App.ViewModels;

/// <summary>
/// 三级列表分组（F2 按级分组）：✅/⚠️ 默认展开、🚫 默认折叠（仅组头计数可见，R02 GWT）；
/// 行按树合计内存降序（组内排序收口在构造，绑定面零逻辑）。
/// </summary>
public sealed class LevelGroup : INotifyPropertyChanged
{
    private bool _isExpanded;

    public LevelGroup(Level level, IEnumerable<ClassificationRow> rows)
    {
        Level = level;
        // F2：组内按树合计私有提交降序（同值按 PID 稳定序，防扫描间顺序抖动）
        Rows = rows.OrderByDescending(r => r.TreePrivateBytes).ThenBy(r => r.Pid).ToList();
        _isExpanded = level != Level.Protected;
        Title = DisplayText.GroupTitle(level, Rows.Count);
    }

    public Level Level { get; }

    /// <summary>组头标题（含项数计数）。</summary>
    public string Title { get; }

    /// <summary>组折叠态（🚫 默认 false，可展开看原因——R02 GWT）。</summary>
    public bool IsExpanded { get => _isExpanded; set => SetField(ref _isExpanded, value); }

    /// <summary>组内行（树合计降序；组实例随列表整体替换，不逐项变更）。</summary>
    public IReadOnlyList<ClassificationRow> Rows { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        return true;
    }
}
