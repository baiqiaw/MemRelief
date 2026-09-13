using System.ComponentModel;
using MemRelief.App.Text;
using MemRelief.Core.Contracts;

namespace MemRelief.App.ViewModels;

/// <summary>
/// 同名多实例聚合组行（T-28 / issue #46）：同进程名 ≥2 实例聚合为一行（「node.exe ×27 · 树合计 X」），
/// 展开呈现子行（复用 ClassificationRow 模板逐项操作）。
/// 旁路投影：LevelGroup.Rows 保持平铺承载勾选/释放/加白等全部逻辑，本行仅做组级勾选联动与展示。
/// 组级勾选三态：全勾 true / 全不勾 false / 部分 null；置 true/false 写穿子项，null（部分态点击）视为全不勾。
/// </summary>
public sealed class ProcessAggregateRow : INotifyPropertyChanged
{
    private bool? _isChecked;
    private bool _isExpanded;

    public ProcessAggregateRow(string processName, IReadOnlyList<ClassificationRow> rows)
    {
        ProcessName = processName;
        Rows = rows;
        Level = rows[0].Level;
        CanCheck = rows.All(r => r.CanCheck);
        CanWhitelist = rows.Any(r => r.CanWhitelist);
        Count = rows.Count;
        TotalTreeSummary = $"树合计 {DisplayText.TreeMb(rows.Sum(r => r.TreePrivateBytes))}（×{Count}）";
        foreach (var row in rows)
        {
            row.PropertyChanged += OnChildPropertyChanged;
        }
        _isChecked = ComputeState();
    }

    /// <summary>组名（快照进程名原文；加白按名匹配的键）。</summary>
    public string ProcessName { get; }

    /// <summary>实例数。</summary>
    public int Count { get; }

    public Level Level { get; }

    /// <summary>子行（与 LevelGroup.Rows 同引用；释放/加白逻辑以平铺 Rows 为唯一事实源）。</summary>
    public IReadOnlyList<ClassificationRow> Rows { get; }

    /// <summary>复选框可用：仅当全部子项可勾（🚫 集群行禁用，与单行口径一致）。</summary>
    public bool CanCheck { get; }

    /// <summary>右键加白可用（任一子项可加白即整组可加；加白按名匹配，语义即全实例排除）。</summary>
    public bool CanWhitelist { get; }

    /// <summary>组头显示：名称 ×N。</summary>
    public string HeaderText => $"{ProcessName} ×{Count}";

    /// <summary>树合计摘要（子项 TreePrivateBytes 求和，口径 #13）。</summary>
    public string TotalTreeSummary { get; }

    /// <summary>组级勾选三态；写入写穿全部子项（置 null 视为全不勾，承接 WPF 三态点击循环）。</summary>
    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            var target = value ?? false;
            foreach (var row in Rows)
            {
                row.IsChecked = target;
            }
            SetField(ref _isChecked, ComputeState(), nameof(IsChecked));
        }
    }

    /// <summary>组展开态（默认折叠；展开呈现子行，子行自身六要素再各自展开）。</summary>
    public bool IsExpanded { get => _isExpanded; set => SetField(ref _isExpanded, value); }

    /// <summary>右键加白传参：首个可加白子行（白名单按名匹配，排除即全实例）；
    /// 保护级集群无子项可加白 → null 流入命令谓词自然禁用（与单行模板同口径，不抛异常）。</summary>
    public ClassificationRow? WhitelistTarget => Rows.FirstOrDefault(r => r.CanWhitelist);

    private bool? ComputeState() =>
        Rows.All(r => r.IsChecked) ? true
        : Rows.All(r => !r.IsChecked) ? false
        : null;

    private void OnChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClassificationRow.IsChecked))
        {
            SetField(ref _isChecked, ComputeState(), nameof(IsChecked));
        }
    }

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
