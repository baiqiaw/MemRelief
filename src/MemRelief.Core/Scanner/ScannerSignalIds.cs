namespace MemRelief.Core.Scanner;

// SignalFailure.SignalId 编号段（data-contracts §2 T-01 裁决③）：
// 口径表 1–15 为信号采集编号；100+ 为基础字段失败编号段；0 为进程级保留值。

/// <summary>基础字段失败编号段（scanner 采集侧专用，非口径表 1–15）。</summary>
public static class ScannerSignalIds
{
    /// <summary>进程级保留值：OpenProcess 打开失败（AccessDenied/消失）单条覆盖四基础字段。</summary>
    public const int ProcessOpen = 0;

    public const int ExecutablePath = 100;
    public const int CreationTimeUtc = 101;
    public const int PrivateCommittedBytes = 102;
    /// <summary>进程所有者（裸用户名，裁决①）。术语取"所有者"：PRD 口径#4 的"属主"专指窗口归属，勿混。</summary>
    public const int OwnerUser = 103;
}
