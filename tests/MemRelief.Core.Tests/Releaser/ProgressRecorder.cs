using MemRelief.Core.Contracts;
using MemRelief.Core.Releaser;

namespace MemRelief.Core.Tests.Releaser;

/// <summary>
/// 进度事件记录器（共享测试辅助）：挂接 IReleaser.TreeProgress，事后按树读取状态序/全量事件。
/// 合成/取消报告/真机集成三测试类共用单份实现，防复制漂移（cross-review DRY 收口）。
/// 实例无跨测试共享状态，静态互斥不涉及；Execute 前挂接、事后读取与既有口径一致。
/// </summary>
internal sealed class ProgressRecorder
{
    private readonly List<(int Pid, TreeState State)> _events = new();

    /// <summary>挂接到释放器（返回 this，兼容 fluent 链与语句两种用法）。</summary>
    public ProgressRecorder Attach(IReleaser releaser)
    {
        releaser.TreeProgress += (pid, state) =>
        {
            lock (_events)
            {
                _events.Add((pid, state));
            }
        };
        return this;
    }

    public string[] StatesOf(int pid)
    {
        lock (_events)
        {
            return _events.Where(x => x.Pid == pid).Select(x => x.State.ToString()).ToArray();
        }
    }

    public List<(int Pid, TreeState State)> AllStates
    {
        get
        {
            lock (_events)
            {
                return new List<(int, TreeState)>(_events);
            }
        }
    }

    public int TotalCount
    {
        get
        {
            lock (_events)
            {
                return _events.Count;
            }
        }
    }
}
