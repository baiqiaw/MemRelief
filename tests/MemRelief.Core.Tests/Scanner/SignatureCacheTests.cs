using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;
using Xunit;

namespace MemRelief.Core.Tests.Scanner;

// 签名验证缓存单测（口径 #9"结果按路径+mtime 缓存"；scanner.md §6 法级例外）：
// 键=路径+mtime；同键只验一次；mtime 变更即失效重验；缓存仅存验证结论。
public class SignatureCacheTests
{
    private static SignatureVerdict VerdictOf(SignatureStatus status = SignatureStatus.ValidNonMicrosoft) =>
        new(status, "ACME");

    [Fact]
    public void 同路径同mtime_只验一次且返回同结论()
    {
        var cache = new SignatureCache();
        var calls = 0;
        SignatureVerdict Verify(string _) { calls++; return VerdictOf(); }

        var first = cache.GetOrAdd(@"C:\app\a.exe", Mtime(1000), Verify);
        var second = cache.GetOrAdd(@"C:\app\a.exe", Mtime(1000), Verify);

        Assert.Equal(1, calls);
        Assert.Equal(1, cache.Count);
        Assert.Same(first, second);
    }

    [Fact]
    public void mtime变更_缓存失效并重验()
    {
        var cache = new SignatureCache();
        var calls = 0;
        SignatureVerdict Verify(string _) { calls++; return VerdictOf(); }

        cache.GetOrAdd(@"C:\app\a.exe", Mtime(1000), Verify);
        cache.GetOrAdd(@"C:\app\a.exe", Mtime(2000), Verify); // 文件被替换 → mtime 变化 → 旧结论不得复用

        Assert.Equal(2, calls);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void 同mtime不同路径_各自验证互不串()
    {
        var cache = new SignatureCache();
        var verified = new List<string>();
        SignatureVerdict Verify(string path) { verified.Add(path); return VerdictOf(); }

        cache.GetOrAdd(@"C:\app\a.exe", Mtime(1000), Verify);
        cache.GetOrAdd(@"C:\app\b.exe", Mtime(1000), Verify);

        Assert.Equal(2, cache.Count);
        Assert.Equal(new[] { @"C:\app\a.exe", @"C:\app\b.exe" }, verified);
    }

    [Fact]
    public void 并发同键_仅执行一次验证()
    {
        var cache = new SignatureCache();
        var calls = 0;
        SignatureVerdict Verify(string _)
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(50); // 放大窗口：并发到达时其余线程应等 Lazy 结果而非重复验证
            return VerdictOf();
        }

        // 同时起跑，放大并发到达窗口（Lazy 语义下工厂仍须只执行一次）
        using var start = new ManualResetEventSlim(false);
        start.Set();
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            start.Wait();
            cache.GetOrAdd(@"C:\app\a.exe", Mtime(1000), Verify);
        });

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    private static DateTime Mtime(long ticks) => new(ticks, DateTimeKind.Utc);
}
