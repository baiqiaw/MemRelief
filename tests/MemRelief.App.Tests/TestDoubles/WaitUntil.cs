using System.Threading.Tasks;

namespace MemRelief.App.Tests.TestDoubles;

/// <summary>
/// 测试轮询等待助手（全测试工程共用，收敛各文件私拷贝）：条件成立即返回；
/// 超时默认静默返回（超时后的状态断言由调用方持有，失败信息=真实状态差异）；
/// throwOnTimeout=true 时超时抛 TimeoutException（铺态屏障用——失败直接指向等待本身，#56）。
/// </summary>
public static class WaitUntil
{
    public static async Task ForAsync(Func<bool> condition, TimeSpan? timeout = null, int stepMs = 20,
        bool throwOnTimeout = false, string? label = null)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow + wait;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(stepMs).ConfigureAwait(false);
        }

        if (throwOnTimeout && !condition())
        {
            throw new TimeoutException($"{label ?? "等待条件"}在 {wait.TotalSeconds:0.#}s 内未满足");
        }
    }
}
