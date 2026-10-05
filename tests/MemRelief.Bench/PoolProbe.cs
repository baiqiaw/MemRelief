namespace MemRelief.Bench;

/// <summary>承载实验单条记录：规模、模式、整批墙钟耗时与线程池线程数观测（前/后）。</summary>
public sealed record PoolProbeResult(
    int Trees, string Mode, long TotalMs, int ThreadsBefore, int ThreadsAfter);

/// <summary>
/// 释放链线程池承载实验（issue #40）：复刻 ProcessReleaser 多树并行等待段的两种承载形态，
/// 测整批墙钟耗时随树数的伸缩——
/// <list type="bullet">
/// <item>sync（现状模型）：每树 Task.Run + Thread.Sleep 轮询（ProcessReleaser.RunTree 同构，
/// 等待段占住线程池线程至 waitMs）；</item>
/// <item>async（候选模型）：await Task.Delay 轮询（等待段让出线程，同步前段外的等价改造形态）。</item>
/// </list>
/// 模型口径：真实等待段内每轮含 Win32 探活（WaitExit(0)，微秒级），本模型以时钟比较占位——
/// 差异变量仅为承载方式（阻塞 vs 让出），实验聚焦该变量。两模式轮询结构与
/// ProcessReleaser 等待循环逐行对齐（先探活后睡、剩余预算封顶单轮休眠）。
/// 只做计时与观测输出，不含裁决（T-21 边界同款）。
/// </summary>
public sealed class PoolProbe
{
    /// <summary>
    /// 逐规模 × 逐模式（sync 先于 async）跑一轮，返回记录列表（规模升序、mode 过滤）。
    /// </summary>
    public async Task<IReadOnlyList<PoolProbeResult>> RunAsync(
        IReadOnlyList<int> treeCounts, int waitMs, int pollMs, string mode)
    {
        var results = new List<PoolProbeResult>();
        foreach (var trees in treeCounts.OrderBy(x => x))
        {
            if (mode is "sync" or "both")
            {
                results.Add(await RunSingleAsync(trees, waitMs, pollMs, "sync").ConfigureAwait(false));
            }

            if (mode is "async" or "both")
            {
                results.Add(await RunSingleAsync(trees, waitMs, pollMs, "async").ConfigureAwait(false));
            }
        }

        return results;
    }

    private static async Task<PoolProbeResult> RunSingleAsync(int trees, int waitMs, int pollMs, string mode)
    {
        var threadsBefore = ThreadPool.ThreadCount;
        var total = System.Diagnostics.Stopwatch.StartNew();

        Task batch;
        if (mode == "sync")
        {
            // 现状模型：每树一个线程池任务，等待段同步阻塞（Thread.Sleep）
            batch = Task.WhenAll(Enumerable.Range(0, trees).Select(_ => Task.Run(() => SyncPoll(waitMs, pollMs))));
        }
        else
        {
            // 候选模型：每树一个异步任务，等待段让出线程（await Task.Delay）
            batch = Task.WhenAll(Enumerable.Range(0, trees).Select(_ => AsyncPoll(waitMs, pollMs)));
        }

        await batch.ConfigureAwait(false);
        total.Stop();
        return new PoolProbeResult(trees, mode, total.ElapsedMilliseconds, threadsBefore, ThreadPool.ThreadCount);
    }

    /// <summary>同步轮询等待（Thread.Sleep，ProcessReleaser 等待循环同构）。</summary>
    private static void SyncPoll(int waitMs, int pollMs)
    {
        var deadline = Environment.TickCount64 + waitMs;
        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                return;
            }

            Thread.Sleep((int)Math.Min(pollMs, remaining));
        }
    }

    /// <summary>异步轮询等待（Task.Delay，让出线程的同构形态）。</summary>
    private static async Task AsyncPoll(int waitMs, int pollMs)
    {
        var deadline = Environment.TickCount64 + waitMs;
        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                return;
            }

            await Task.Delay((int)Math.Min(pollMs, remaining)).ConfigureAwait(false);
        }
    }
}
