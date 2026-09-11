using MemRelief.Core.Contracts;
using MemRelief.Core.Scanner;
using Xunit;

namespace MemRelief.Core.Tests.Scanner;

// 验签四分支映射单测（口径 #9，纯函数）：HRESULT/签名者名 → SignatureStatus 六值。
// 分支口径：微软→Microsoft；有效非微软→ValidNonMicrosoft（SignerName 必填）；
// 无效/无签名→Invalid/Unsigned（不额外降级）；Access Denied/验不了→Unverifiable（rules 按受保护处理）。
public class SignatureRulesTests
{
    // ---------- 签名者名判定（Microsoft vs ValidNonMicrosoft，S_OK 后） ----------

    [Theory]
    [InlineData("Microsoft Windows")]
    [InlineData("MICROSOFT Corporation")]
    [InlineData("Microsoft Corporation Third Party Marketplace")]
    public void 微软系签名者_判为微软(string signerName)
    {
        Assert.True(SignatureRules.IsMicrosoftSigner(signerName));
    }

    [Theory]
    [InlineData("360 Total Security")]
    [InlineData("Tencent Technology")]
    [InlineData(null)]
    [InlineData("")]
    public void 非微软或空签名者_判非微软(string? signerName)
    {
        Assert.False(SignatureRules.IsMicrosoftSigner(signerName));
    }

    [Fact]
    public void 成功且微软签名者_映射Microsoft且不携带SignerName()
    {
        var (status, signerName) = SignatureRules.MapTrustSuccess("Microsoft Windows");
        Assert.Equal(SignatureStatus.Microsoft, status);
        Assert.Null(signerName); // SignerName 仅 ValidNonMicrosoft 必填（契约 §1.1）
    }

    [Fact]
    public void 成功且有效非微软_映射ValidNonMicrosoft且携带SignerName()
    {
        var (status, signerName) = SignatureRules.MapTrustSuccess("ACME Software");
        Assert.Equal(SignatureStatus.ValidNonMicrosoft, status);
        Assert.Equal("ACME Software", signerName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 成功但签名者不可读_保守映射Unverifiable(string? signerName)
    {
        // 有效签名但签名者取不到：不可确证微软/非微软，按"验不了"保守处理（rules → 受保护）
        Assert.Equal(SignatureStatus.Unverifiable, SignatureRules.MapTrustSuccess(signerName).Status);
    }

    // ---------- 失败 HRESULT → 四分支（Unsigned / Invalid / Unverifiable） ----------

    [Fact]
    public void 无签名_映射Unsigned()
    {
        Assert.Equal(SignatureStatus.Unsigned, SignatureRules.MapTrustFailure(unchecked((int)0x800B0100))); // TRUST_E_NOSIGNATURE
    }

    [Theory]
    [InlineData(unchecked((int)0x80096010))] // TRUST_E_BAD_DIGEST：摘要不符（文件被篡改）
    [InlineData(unchecked((int)0x80092010))] // CRYPT_E_REVOKED：证书已吊销
    [InlineData(unchecked((int)0x80090006))] // NTE_BAD_SIGNATURE：密码层验签失败
    [InlineData(unchecked((int)0x800B0101))] // CERT_E_EXPIRED
    [InlineData(unchecked((int)0x800B0109))] // CERT_E_UNTRUSTEDROOT
    [InlineData(unchecked((int)0x800B010C))] // CERT_E_REVOKED
    [InlineData(unchecked((int)0x800B010B))] // TRUST_E_FAIL：通用信任失败
    [InlineData(unchecked((int)0x800B0110))] // CERT_E_WRONG_USAGE
    [InlineData(unchecked((int)0x800B0111))] // TRUST_E_EXPLICIT_DISTRUST
    [InlineData(unchecked((int)0x800B0004))] // TRUST_E_SUBJECT_NOT_TRUSTED
    public void 信任判定确凿失败_映射Invalid(int hresult)
    {
        Assert.Equal(SignatureStatus.Invalid, SignatureRules.MapTrustFailure(hresult));
    }

    [Fact]
    public void CERT_E连续段_全部映射Invalid()
    {
        // 0x800B0101..0x800B010C 为 CERT_E_EXPIRED..CERT_E_REVOKED 连续段（winerror.h；0x800B010B=TRUST_E_FAIL 亦落此段）
        for (var hr = unchecked((int)0x800B0101); hr <= unchecked((int)0x800B010C); hr++)
        {
            Assert.Equal(SignatureStatus.Invalid, SignatureRules.MapTrustFailure(hr));
        }
    }

    [Fact]
    public void 吊销检查失败_保守映射Unverifiable()
    {
        // CERT_E_REVOCATION_FAILURE(0x800B010E) 在 REVOKE_NONE 通道下不可达；若经策略出现属"验不了"性质，保守落 Unverifiable
        Assert.Equal(SignatureStatus.Unverifiable, SignatureRules.MapTrustFailure(unchecked((int)0x800B010E)));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005))] // HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED)：文件不可读
    [InlineData(unchecked((int)0x80070002))] // HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)：文件已消失
    [InlineData(unchecked((int)0x80070020))] // HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION)：文件被独占锁定
    [InlineData(unchecked((int)0x800B0001))] // TRUST_E_PROVIDER_UNKNOWN：信任提供方未知
    [InlineData(unchecked((int)0x80092026))] // CRYPT_E_SECURITY_SETTINGS：策略阻止验证
    [InlineData(unchecked((int)0xE0001234))] // 未收录门类的未知错误码
    public void 文件不可达或未知失败_保守映射Unverifiable(int hresult)
    {
        // 未知/环境性失败不落 Invalid/Unsigned（二者"正常参与分级"），保守走 Unverifiable=受保护
        Assert.Equal(SignatureStatus.Unverifiable, SignatureRules.MapTrustFailure(hresult));
    }
}
