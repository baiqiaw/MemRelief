using MemRelief.Core.Contracts;

namespace MemRelief.Core.Scanner;

/// <summary>
/// 验签四分支映射（口径 #9，纯函数）：WinVerifyTrust 结果 → SignatureStatus 六值。
/// 分支：微软 → Microsoft（🚫候选）；有效非微软 → ValidNonMicrosoft（SignerName 必填，正常参与分级）；
/// 无效/无签名 → Invalid/Unsigned（不额外降级，正常参与）；Access Denied/验不了 → Unverifiable
/// （rules 按受保护处理）。零 I/O 零状态，同输入同输出。
/// </summary>
internal static class SignatureRules
{
    // WinVerifyTrust HRESULT 常量（winerror.h；值与 SDK 头文件逐一核对）。未收录错误码一律保守落 Unverifiable。
    private const int HrTrustENoSignature = unchecked((int)0x800B0100);
    private const int HrTrustESubjectNotTrusted = unchecked((int)0x800B0004);
    private const int HrCertEExpired = unchecked((int)0x800B0101);
    private const int HrCertERevoked = unchecked((int)0x800B010C);
    private const int HrCertEWrongUsage = unchecked((int)0x800B0110);
    private const int HrTrustEExplicitDistrust = unchecked((int)0x800B0111);
    private const int HrTrustEBadDigest = unchecked((int)0x80096010);
    private const int HrCryptERevoked = unchecked((int)0x80092010);
    private const int HrNteBadSignature = unchecked((int)0x80090006);

    /// <summary>
    /// 微软签名者判定（v1 口径）：签名者显示名含 "microsoft"（OrdinalIgnoreCase）即微软。
    /// 已知边界（v1 接受）：显示名经 CA 实名校验，冒名概率低；误判方向=多保护（微软级→🚫候选），误杀风险为零。
    /// </summary>
    public static bool IsMicrosoftSigner(string? signerName) =>
        !string.IsNullOrEmpty(signerName) && signerName.Contains("microsoft", StringComparison.OrdinalIgnoreCase);

    /// <summary>WinVerifyTrust 成功（S_OK）+ 签名者显示名 → 状态与 SignerName（仅 ValidNonMicrosoft 携带，契约 §1.1）。</summary>
    public static (SignatureStatus Status, string? SignerName) MapTrustSuccess(string? signerName)
    {
        if (IsMicrosoftSigner(signerName))
        {
            return (SignatureStatus.Microsoft, null);
        }
        // 有签名但签名者不可读：微软/非微软均不可确证，按"验不了"保守处理（rules → 受保护）
        return string.IsNullOrWhiteSpace(signerName)
            ? (SignatureStatus.Unverifiable, null)
            : (SignatureStatus.ValidNonMicrosoft, signerName);
    }

    /// <summary>WinVerifyTrust 失败 HRESULT → Unsigned / Invalid / Unverifiable（恒不落 Microsoft/ValidNonMicrosoft）。
    /// 保守原则：只有"信任判定已确凿完成且失败"才落 Invalid（不额外降级）；环境性/未知失败落 Unverifiable（受保护）。</summary>
    public static SignatureStatus MapTrustFailure(int hresult)
    {
        if (hresult == HrTrustENoSignature)
        {
            return SignatureStatus.Unsigned; // 文件可读且完整解析、无内嵌签名 → 无签名（不额外降级）
        }
        if (hresult == HrTrustESubjectNotTrusted
            || hresult == HrTrustEBadDigest
            || hresult == HrCryptERevoked
            || hresult == HrNteBadSignature
            || hresult == HrCertEWrongUsage
            || hresult == HrTrustEExplicitDistrust
            || (hresult >= HrCertEExpired && hresult <= HrCertERevoked)) // CERT_E_EXPIRED..CERT_E_REVOKED 连续段（0x800B010B=TRUST_E_FAIL 亦落此段，winerror.h）
        {
            return SignatureStatus.Invalid; // 证书链/摘要/吊销/显式不信任等确凿失败
        }
        // Win32 文件不可达（Access Denied/已删除/共享冲突）、信任提供方未知、策略阻止、未知门类 → 验不了
        return SignatureStatus.Unverifiable;
    }
}
