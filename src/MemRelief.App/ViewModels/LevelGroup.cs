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
        // 组内合计按树覆盖去重：父也在组内的行已被其父行树合计覆盖（计 0），只统计组内顶层行——
        // 结果恰等于实际释放量（释放按整树计，含未进组的深层后代）
        var pidSet = Rows.Select(r => r.Pid).ToHashSet();
        var totalBytes = Rows.Where(r => !pidSet.Contains(r.ParentPid)).Sum(r => r.TreePrivateBytes);
        Title = DisplayText.GroupTitle(level, Rows.Count, totalBytes);
        // T-28 同名聚合（issue #46）：同名 ≥2 实例聚合为组行；DisplayRows=单实例行+聚合组行按 Rows 序混排
        //（聚合行替换其首成员位置）。Rows 保持平铺，勾选/释放/加白逻辑仍以平铺行为唯一事实源。
        Aggregates = Rows.GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .Select(g => new ProcessAggregateRow(g.Key, g.ToList()))
            .ToList();
        var aggregateByPid = Aggregates.SelectMany(a => a.Rows.Select(r => (Pid: r.Pid, Aggregate: a)))
            .ToDictionary(x => x.Pid, x => x.Aggregate);
        var emittedAggregates = new HashSet<ProcessAggregateRow>();
        var display = new List<object>();
        foreach (var row in Rows)
        {
            if (aggregateByPid.TryGetValue(row.Pid, out var aggregate))
            {
                if (emittedAggregates.Add(aggregate))
                {
                    display.Add(aggregate);   // 聚合行替换其首成员位置，同组其余成员不再出现
                }
            }
            else
            {
                display.Add(row);
            }
        }
        DisplayRows = display;
    }

    public Level Level { get; }

    /// <summary>组头标题（含项数计数）。</summary>
    public string Title { get; }

    /// <summary>组折叠态（🚫 默认 false，可展开看原因——R02 GWT）。</summary>
    public bool IsExpanded { get => _isExpanded; set => SetField(ref _isExpanded, value); }

    /// <summary>组内行（树合计降序；组实例随列表整体替换，不逐项变更）。</summary>
    public IReadOnlyList<ClassificationRow> Rows { get; }

    /// <summary>同名聚合组行（T-28：同名 ≥2 实例；勾选/加白逻辑的旁路投影，非事实源）。</summary>
    public IReadOnlyList<ProcessAggregateRow> Aggregates { get; }

    /// <summary>渲染行序列（单实例行+聚合组行按 Rows 序混排；XAML 绑定面，经 DataType 选模板）。</summary>
    public IReadOnlyList<object> DisplayRows { get; }

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
