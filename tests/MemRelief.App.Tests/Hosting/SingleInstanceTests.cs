using MemRelief.App.Hosting;

namespace MemRelief.App.Tests.Hosting;

/// <summary>
/// 单实例互斥测试（T-27，data-contracts §2 ③.s4 裁决⑨）：命名 Mutex 即夺即判、
/// 携重启参数有限等待重试（防提权新实例误判“已启动”静默退出）、遗弃互斥接管、Dispose 让位。
/// 功能测试用注入名隔离（xUnit 并行防串扰）；默认名契约单独钉死。
/// </summary>
public class SingleInstanceTests
{
    private static string UniqueName() => @"Local\MemRelief.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void 默认互斥名_钉死契约()
    {
        Assert.Equal(@"Local\MemRelief.SingleInstance", SingleInstanceGuard.DefaultMutexName);
    }

    [Fact]
    public void 首实例_夺锁成功为主实例()
    {
        using var guard = SingleInstanceGuard.Acquire(mutexName: UniqueName());

        Assert.True(guard.IsPrimary);
    }

    [Fact]
    public void 二次启动_无重启参数_即夺即判失败()
    {
        var name = UniqueName();
        using var first = SingleInstanceGuard.Acquire(mutexName: name);

        using var second = SingleInstanceGuard.Acquire(mutexName: name);

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary); // 调用方激活既有窗口后退出，不静默双实例
    }

    [Fact]
    public async Task 携重启参数_旧实例退出后有限等待内夺锁成功()
    {
        var name = UniqueName();
        // 旧实例 300ms 后退出让位（提权重启链路：旧实例先启动新实例再退出）。
        // 互斥线程亲和：持有/释放须固定同一线程，async 跨线程延续会让 ReleaseMutex 落在非持有线程
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(() =>
        {
            using var old = SingleInstanceGuard.Acquire(mutexName: name);
            ready.SetResult(); // 旧实例已持锁
            Thread.Sleep(300);
        });
        await ready.Task;

        using var guard = SingleInstanceGuard.Acquire(waitForExisting: true, mutexName: name);

        await holder;
        Assert.True(guard.IsPrimary); // 有限等待重试等到让位，未误判“已启动”静默退出
    }

    [Fact]
    public async Task 携重启参数_等待超时_旧实例仍在_判定失败()
    {
        var name = UniqueName();
        // 旧实例在独立线程持锁不释放（互斥线程亲和：同线程二次 WaitOne 递归成功，必须异线程持有）
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);
        var holder = Task.Run(() =>
        {
            using var first = SingleInstanceGuard.Acquire(mutexName: name);
            ready.SetResult();
            release.Wait(TimeSpan.FromSeconds(10)); // 持有不放
        });
        await ready.Task;

        using var guard = SingleInstanceGuard.Acquire(
            waitForExisting: true, mutexName: name, waitBudget: TimeSpan.FromMilliseconds(300));

        release.Set();
        await holder;
        Assert.False(guard.IsPrimary); // 超时不双实例：同样走激活既有窗口退出
    }

    [Fact]
    public void 旧实例异常退出遗弃互斥_新实例接管成功()
    {
        // 对抗性：旧实例线程持锁异常退出（未 Release）→ 互斥进入遗弃态，WaitOne 抛 AbandonedMutexException
        var name = UniqueName();
        var holder = new Thread(() =>
        {
            var abandoned = new Mutex(true, name, out _);
            GC.KeepAlive(abandoned); // 线程退出即遗弃，不显式释放
        });
        holder.Start();
        holder.Join(); // 线程退出 → 互斥遗弃

        using var guard = SingleInstanceGuard.Acquire(waitForExisting: true, mutexName: name);

        Assert.True(guard.IsPrimary); // 遗弃=旧实例已不在，接管而非误判退出
    }

    [Fact]
    public void 主实例Dispose让位后_新实例可夺锁()
    {
        var name = UniqueName();
        var first = SingleInstanceGuard.Acquire(mutexName: name);
        first.Dispose();

        using var second = SingleInstanceGuard.Acquire(mutexName: name);

        Assert.True(second.IsPrimary);
    }
}
