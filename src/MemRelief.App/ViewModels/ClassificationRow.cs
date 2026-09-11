using System.ComponentModel;
using MemRelief.App.Text;
using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;

namespace MemRelief.App.ViewModels;

/// <summary>
/// 快照进程索引（整组行共享一次构建，O(n)）：按 PID 定位 + 按父 PID 分组；
/// 重复 PID（契约违约脏数据）按首条收口不抛——投影层防脏数据拖垮整列表。
/// </summary>
internal sealed class TreeIndex
{
    private readonly IReadOnlyDictionary<int, ProcessSnapshot> _byPid;
    private readonly ILookup<int, ProcessSnapshot> _children;

    private TreeIndex(
        IReadOnlyDictionary<int, ProcessSnapshot> byPid,
        ILookup<int, ProcessSnapshot> children)
    {
        _byPid = byPid;
        _children = children;
    }

    internal static TreeIndex Build(ScanResult snapshot) => new(
        snapshot.Snapshots.GroupBy(s => s.Pid).ToDictionary(g => g.Key, g => g.First()),
        snapshot.Snapshots.ToLookup(s => s.ParentPid));

    internal bool TryGet(int pid, out ProcessSnapshot snapshot) => _byPid.TryGetValue(pid, out snapshot!);

    /// <summary>进程树渲染：从快照父子链收集全部后代（缩进文本；PID 环/重复引用只渲染一次，子代按 PID 序稳定输出）。</summary>
    internal IReadOnlyList<string> BuildLines(int rootPid)
    {
        var lines = new List<string>();
        var visited = new HashSet<int>();
        Walk(rootPid, depth: 0);
        return lines;

        void Walk(int pid, int depth)
        {
            if (!_byPid.TryGetValue(pid, out var p) || !visited.Add(pid))
            {
                return; // 防御：环/重复引用只渲染一次，不死循环
            }

            lines.Add($"{new string(' ', depth * 2)}{p.Name} ({p.Pid})");
            foreach (var child in _children[pid].OrderBy(s => s.Pid))
            {
                Walk(child.Pid, depth + 1);
            }
        }
    }
}

/// <summary>
/// 三级列表行（Classification 只读投影，F2 六要素承载）：头行=复选框+主进程名（含孤儿后缀）+树合计；
/// 展开区=原因说明/来源/进程树/拉起提示。勾选默认态：✅ 全勾、其余不勾（PRD F2）；
/// 🚫 行复选框禁用（R02 GWT）；右键加白仅 ✅/⚠️ 可用（PRD F4）。
/// </summary>
public sealed class ClassificationRow : INotifyPropertyChanged
{
    private bool _isChecked;
    private bool _isExpanded;

    /// <summary>整组投影入口（VM 侧共享索引一次构建）；独立构造走单行索引（测试铺态用）。</summary>
    public ClassificationRow(Classification classification, ScanResult snapshot)
        : this(classification, TreeIndex.Build(snapshot))
    {
    }

    internal ClassificationRow(Classification classification, TreeIndex index)
    {
        Pid = classification.Pid;
        Level = classification.Level;
        TreePrivateBytes = classification.TreePrivateBytes;

        index.TryGet(classification.Pid, out var processSnapshot);
        ProcessName = processSnapshot?.Name ?? $"PID {classification.Pid}";
        // 孤儿项（含 PID 复用）主进程名后缀（R02 空值口径）
        var orphan = processSnapshot is not null
            && processSnapshot.Signals.OrphanHint is OrphanHint.ParentDead or OrphanHint.PidReused;
        DisplayName = orphan ? ProcessName + DisplayText.OrphanSuffix : ProcessName;
        IsChecked = Level == Level.Recommend;
        CanCheck = Level != Level.Protected;
        CanWhitelist = Level is Level.Recommend or Level.Caution;
        ReasonText = DisplayText.Reason(classification.Bases);
        SourceText = DisplayText.Source(classification.SourceEntries);
        ReviveHint = DisplayText.ReviveHint(classification.WouldBeRevived, classification.SourceEntries);
        TreeSummary = $"树合计 {DisplayText.TreeMb(classification.TreePrivateBytes)}";
        TreeLines = index.BuildLines(classification.Pid);
    }

    public int Pid { get; }

    /// <summary>主进程名（快照原文；加白按名匹配的键）。</summary>
    public string ProcessName { get; }

    public Level Level { get; }

    public long TreePrivateBytes { get; }

    /// <summary>头行显示名：孤儿项带“(父进程已退出)”后缀（R02 空值口径）。</summary>
    public string DisplayName { get; }

    /// <summary>勾选默认态在构造内置位（✅ 全勾）；🚫 行恒不勾且禁用。</summary>
    public bool IsChecked { get => _isChecked; set => SetField(ref _isChecked, value); }

    /// <summary>复选框可用（🚫 保护级行禁用，R02 GWT“复选框禁用不可勾选”）。</summary>
    public bool CanCheck { get; }

    /// <summary>右键加白可用（仅 ✅/⚠️ 级，PRD F4；🚫 保护名单加白无意义且制造绕过歧义）。</summary>
    public bool CanWhitelist { get; }

    /// <summary>行详情展开态（默认折叠；与组折叠独立）。</summary>
    public bool IsExpanded { get => _isExpanded; set => SetField(ref _isExpanded, value); }

    /// <summary>原因说明（判定依据中文文案，逐条换行）。</summary>
    public string ReasonText { get; }

    /// <summary>来源类型+条目名；无四类来源=“无（手动启动）”。</summary>
    public string SourceText { get; }

    /// <summary>“杀掉后是否会被拉起”提示（R02 六要素之六，三态）。</summary>
    public string ReviveHint { get; }

    /// <summary>树合计内存摘要（口径 #13，Classification.TreePrivateBytes 唯一承载）。</summary>
    public string TreeSummary { get; }

    /// <summary>进程树父子层级行（缩进文本；孤儿仅自身+后代）。</summary>
    public IReadOnlyList<string> TreeLines { get; }

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
