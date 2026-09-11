using System.Collections.Concurrent;
using MemRelief.Core.Contracts;

namespace MemRelief.Core.Scanner;

/// <summary>缓存键（口径 #9"结果按路径+mtime"）：可执行完整路径 + 文件最后写入时间（Utc）。</summary>
internal readonly record struct SignatureCacheKey(string Path, DateTime MtimeUtc);

/// <summary>单文件验签结论。缓存仅存验证结论，不存判定结果（scanner.md §6 法级例外口径）。</summary>
internal sealed record SignatureVerdict(SignatureStatus Status, string? SignerName);

/// <summary>
/// 签名验证结果缓存（scanner.md §6 法级例外：scanner 除本缓存外不持有跨快照可变状态）。
/// Lazy 包裹保证并发同键只执行一次 WinVerifyTrust；实例生命周期=Scanner（应用会话内有效），
/// 仅内存不落盘（storage 法：除白名单/日志外零文件写入）。
/// </summary>
internal sealed class SignatureCache
{
    private readonly ConcurrentDictionary<SignatureCacheKey, Lazy<SignatureVerdict>> _entries = new();

    /// <summary>取缓存结论或执行验证（键=路径+mtime；mtime 变更即自然失效为新键，旧结论不删除、由实例生命周期回收）。
    /// Lazy 工厂内兜底异常 → Unverifiable：防"工厂异常被 Lazy 固化、同键会话期反复重抛"（异常=验不了，与文件消失同向）。</summary>
    public SignatureVerdict GetOrAdd(string path, DateTime mtimeUtc, Func<string, SignatureVerdict> verify) =>
        _entries.GetOrAdd(
            new SignatureCacheKey(path, mtimeUtc),
            key => new Lazy<SignatureVerdict>(() =>
            {
                try
                {
                    return verify(key.Path);
                }
                catch (Exception)
                {
                    return new SignatureVerdict(SignatureStatus.Unverifiable, null);
                }
            })).Value;

    /// <summary>当前缓存条目数（测试与诊断用）。</summary>
    public int Count => _entries.Count;
}
