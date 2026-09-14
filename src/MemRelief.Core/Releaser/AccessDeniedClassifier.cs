using MemRelief.Core.Contracts;

namespace MemRelief.Core.Releaser;

/// <summary>
/// Access Denied 权限二分（PRD F3-7，releaser.md §2，T-10）：底层同为拒绝访问的失败按
/// "目标进程所有者/令牌类型"区分——非当前用户/服务/PPL → NeedsElevation；
/// 当前用户且非服务仍失败 → Blocked（附系统错误码）。
/// 判定输入 = 目标所有者裸名（执行期令牌读取优先、快照 OwnerUser 回退，T-01 裁决①格式口径）
/// 对当前用户名（OrdinalIgnoreCase）。两路径证据强度不同（cross-review 收口）：
/// 强杀拒绝路径身份已校验（名称+创建时间），快照所有者即存活进程所有者；
/// 打开受拒路径无执行期佐证可做，快照所有者可能因 PID 复用过期（扫描与释放之间），
/// 其结果属"基于扫描快照的尽力判定"，Reason 注明判定依据。
/// 服务变体（issue #39-2，2026-09-14 TL 裁决立项收敛）：以当前用户身份运行的服务归 NeedsElevation
/// （服务进程受 SCM/会话隔离保护，拒绝访问非"用户态可解"场景，"被拦截"不可断言；判定输入=快照
/// ServiceName，口径 #8）。非当前用户/受保护进程（PPL）仍由所有者比对天然覆盖。
/// 所有者不可读（两源皆 null）→ NeedsElevation（fail-safe：无法给出"当前用户"的正向证据，
/// "被拦截"不可断言；"需管理员"是用户可执行动作）。
/// 非拒绝访问错误不经本分类（机械事实 Blocked，由调用方直出）。
/// </summary>
internal static class AccessDeniedClassifier
{
    /// <summary>
    /// 二分判定。<paramref name="action"/> = 人读动作描述（"打开进程"/"强制结束"），
    /// 拼入 Reason 首段；<paramref name="win32Error"/> 恒为拒绝访问码（调用方保证）；
    /// <paramref name="ownerSourceNote"/> = 判定依据注记（人读，拼入 Reason 尾段），
    /// 打开受拒路径传快照佐证说明，强杀路径传空（令牌执行期读取即最强佐证）；
    /// <paramref name="serviceName"/> = 快照 ServiceName（口径 #8，null=非服务），服务收敛判定输入。
    /// </summary>
    public static (ReleaseItemOutcome Outcome, string Reason) Classify(
        string? ownerUser, string? currentUserName, int win32Error, string action,
        string ownerSourceNote = "", string? serviceName = null)
    {
        var note = ownerSourceNote.Length > 0 ? $"（{ownerSourceNote}）" : string.Empty;
        if (ownerUser is null)
        {
            return (ReleaseItemOutcome.NeedsElevation,
                $"{action}被拒（Win32 错误 {win32Error}）：目标所有者不可读，按需管理员权限处理{note}");
        }

        if (string.Equals(ownerUser, currentUserName, StringComparison.OrdinalIgnoreCase))
        {
            if (serviceName is not null)
            {
                return (ReleaseItemOutcome.NeedsElevation,
                    $"{action}被拒（Win32 错误 {win32Error}）：目标为当前用户运行的服务（{serviceName}），需管理员权限{note}");
            }

            return (ReleaseItemOutcome.Blocked,
                $"{action}被拒（Win32 错误 {win32Error}）：目标属当前用户（{ownerUser}），被拦截{note}");
        }

        return (ReleaseItemOutcome.NeedsElevation,
            $"{action}被拒（Win32 错误 {win32Error}）：目标属其他用户/服务/受保护进程（{ownerUser}），需管理员权限{note}");
    }
}
