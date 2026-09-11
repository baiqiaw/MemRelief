using System.Threading.Tasks;

namespace MemRelief.App.Tests.TestDoubles;

/// <summary>
/// 测试轮询等待助手（全测试工程共用，收敛各文件私拷贝）：条件成立即返回；
/// 超时静默返回（超时后的状态断言由调用方持有，失败信息=真实状态差异）。
/// </summary>
public static class WaitUntil
{
    public static async Task ForAsync(Func<bool> condition, TimeSpan? timeout = null, int stepMs = 20)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(stepMs).ConfigureAwait(false);
        }
    }
}
