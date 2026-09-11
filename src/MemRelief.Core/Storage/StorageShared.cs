namespace MemRelief.Core.Storage;

// storage 域共享单点实现（cross-review DRY 收敛，T-12）。
// 两 store（WhitelistStore/ReleaseLogStore）共用以下不变量，禁各持拷贝：
//   ——用户数据目录锚点：法-4 两自有数据文件同目录、提权重启后同目录（storage §4.1），漂移后果为
//     白名单与日志静默分裂两目录，故实现必须单点；
//   ——时区归一：Local 真转 Utc；Unspecified 按 Utc 钟面处理（WhitelistStore.AddedAtUtc 与
//     ReleaseLogStore 时间字段同款语义，data-contracts 契约时间一律 Utc）。

internal static class StorageShared
{
    /// <summary>用户数据目录（LocalApplicationData，按用户、与提权无关）。</summary>
    internal static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductInfo.Name);

    /// <summary>时区归一：Kind=Local 真转 Utc；Kind=Unspecified 按 Utc 钟面处理（不触碰 Kind=Utc）。</summary>
    internal static DateTime NormalizeUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
