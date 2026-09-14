using MemRelief.Core.Contracts;
using MemRelief.Core.Releaser;
using Xunit;

namespace MemRelief.Core.Tests.Releaser;

// Access Denied 权限二分纯函数单测（PRD F3-7，T-10）：
// 非当前用户/服务/PPL → NeedsElevation；当前用户且非服务仍失败 → Blocked（附错误码）；
// 所有者不可读（快照与令牌两源皆 null）→ NeedsElevation（fail-safe，"被拦截"需当前用户正向证据）。
public class AccessDeniedClassifierTests
{
    private const string CurrentUser = "tester";

    // —— 二分主轴：所有者比对（裸名，OrdinalIgnoreCase——T-01 裁决①格式口径） ——

    [Fact]
    public void 所有者非当前用户_NeedsElevation()
    {
        var (outcome, reason) = AccessDeniedClassifier.Classify("other", CurrentUser, 5, "强制结束");
        Assert.Equal(ReleaseItemOutcome.NeedsElevation, outcome);
        Assert.Contains("需管理员", reason);
        Assert.Contains("other", reason);
    }

    [Fact]
    public void 所有者为当前用户_大小写不敏感_Blocked()
    {
        var (outcome, reason) = AccessDeniedClassifier.Classify("Tester", CurrentUser, 5, "强制结束");
        Assert.Equal(ReleaseItemOutcome.Blocked, outcome);
        Assert.Contains("被拦截", reason);
    }

    // —— 服务收敛（issue #39-2，2026-09-14 TL 裁决立项）：以当前用户身份运行的服务 → NeedsElevation ——

    [Fact]
    public void 当前用户运行的服务_NeedsElevation_注明服务名()
    {
        // 服务进程受 SCM/会话隔离保护，拒绝访问非"用户态拦截"可断言场景；快照 ServiceName（口径 #8）为判定输入
        var (outcome, reason) = AccessDeniedClassifier.Classify("Tester", CurrentUser, 5, "强制结束", serviceName: "svc-x");
        Assert.Equal(ReleaseItemOutcome.NeedsElevation, outcome);
        Assert.Contains("svc-x", reason);
        Assert.Contains("服务", reason);
    }

    [Fact]
    public void 当前用户非服务_不受服务收敛影响_Blocked()
    {
        var (outcome, _) = AccessDeniedClassifier.Classify("Tester", CurrentUser, 5, "强制结束", serviceName: null);
        Assert.Equal(ReleaseItemOutcome.Blocked, outcome);
    }

    // —— 令牌/快照两源皆不可读：fail-safe → NeedsElevation ——

    [Fact]
    public void 所有者不可读_NeedsElevation_如实注明()
    {
        var (outcome, reason) = AccessDeniedClassifier.Classify(null, CurrentUser, 5, "打开进程");
        Assert.Equal(ReleaseItemOutcome.NeedsElevation, outcome);
        Assert.Contains("不可读", reason);
    }

    // —— 人读 Reason 携带机械事实（动作 + 系统错误码，PRD F3-7"被拦截（附系统错误码）"） ——

    [Fact]
    public void 理由携带动作与系统错误码()
    {
        var (_, reason) = AccessDeniedClassifier.Classify("other", CurrentUser, 5, "强制结束");
        Assert.Contains("强制结束", reason);
        Assert.Contains("5", reason);
    }

    // —— 当前用户名异常（空）：比对不成立 → NeedsElevation（fail-safe 同向） ——

    [Fact]
    public void 当前用户名缺失_按NeedsElevation兜底()
    {
        var (outcome, _) = AccessDeniedClassifier.Classify("someone", null, 5, "强制结束");
        Assert.Equal(ReleaseItemOutcome.NeedsElevation, outcome);
    }
}
