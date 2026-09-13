namespace MemRelief.App.Hosting;

/// <summary>
/// 单实例互斥（T-27，data-contracts §2 ③.s4 裁决⑨）：命名 Mutex `Local\MemRelief.SingleInstance`
/// （Local 前缀=按会话隔离，多用户会话各自单实例）。
/// 普通二次启动：即夺即判，失败由调用方激活既有窗口后退出；
/// 携重启参数启动（提权重启链路）：对互斥**有限等待重试**——旧实例先拉起新实例再退出，新实例须等到让位，
/// 防误判“已启动”静默退出（裁决⑨）；等待超时同样按“已有实例”退出（不双实例）。
/// 互斥随主实例生命周期持有（App.Exit 时 Dispose 让位）。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string DefaultMutexName = @"Local\MemRelief.SingleInstance";

    private readonly Mutex _mutex;

    private SingleInstanceGuard(Mutex mutex, bool isPrimary)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
    }

    /// <summary>是否主实例（true=继续启动；false=已有实例，调用方激活既有窗口后退出）。</summary>
    public bool IsPrimary { get; }

    /// <summary>
    /// 夺取单实例互斥。waitForExisting=false 即夺即判；true 时对已存在互斥做有限等待
    /// （预算默认 10s，200ms 步进重试；旧实例退出即让位）。
    /// mutexName/waitBudget 可注入（功能测试隔离与超时路径驱动；生产用默认值）。
    /// </summary>
    public static SingleInstanceGuard Acquire(
        bool waitForExisting = false,
        string? mutexName = null,
        TimeSpan? waitBudget = null)
    {
        var name = mutexName ?? DefaultMutexName;
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        if (createdNew)
        {
            return new SingleInstanceGuard(mutex, isPrimary: true);
        }

        if (!waitForExisting)
        {
            return new SingleInstanceGuard(mutex, isPrimary: false);
        }

        var deadline = DateTime.UtcNow + (waitBudget ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (mutex.WaitOne(TimeSpan.FromMilliseconds(200)))
                {
                    return new SingleInstanceGuard(mutex, isPrimary: true);
                }
            }
            catch (AbandonedMutexException)
            {
                // 旧实例异常退出未释放即遗弃互斥：WaitOne 抛异常但所有权已授予——旧实例已不在，接管
                return new SingleInstanceGuard(mutex, isPrimary: true);
            }
        }

        return new SingleInstanceGuard(mutex, isPrimary: false); // 超时：旧实例仍在，不双实例
    }

    /// <summary>释放互斥让位（仅主实例持有所权；非主实例直接释放句柄）。</summary>
    public void Dispose()
    {
        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 非持有线程释放（防御）：句柄释放已足以让位
            }
        }

        _mutex.Dispose();
    }
}
