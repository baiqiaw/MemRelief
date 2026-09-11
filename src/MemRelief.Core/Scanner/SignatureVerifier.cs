using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;
using MemRelief.Core.Contracts;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.WinTrust;

namespace MemRelief.Core.Scanner;

/// <summary>
/// WinVerifyTrust 薄通道（口径 #9 采集侧）：单文件信任验证 + 签名者显示名提取。
/// 覆盖率豁免（ExcludeFromCodeCoverage，与 NativeProcessEnumerator/WmiCommandLineSource 同口径）：
/// 纯互操作样板+managed 包装，无判定/映射逻辑（四分支映射在 SignatureRules 纯函数，单测承载）；
/// 正常路径由真机集成测试实跑验证（SignatureVerificationIntegrationTests）。
/// 通道口径（v1 已知边界，均有方向性论证）：
/// ① 仅内嵌签名（WTD_CHOICE_FILE）：catalog 签名文件落 Unsigned（不额外降级），v1 接受；
/// ② 吊销检查关闭（WTD_REVOKE_NONE）：防 CRL 网络往返挂起击穿 CollectSignatures ≤0.5s 预算；
/// ③ 无 UI（WTD_UI_NONE）、一次性验证（WTD_STATEACTION_IGNORE，无状态句柄需关闭）；
/// ④ 签名者名经 X509Certificate.CreateFromSignedFile 取第一签名（多签名文件取首个）。
/// </summary>
[ExcludeFromCodeCoverage]
internal static class SignatureVerifier
{
    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2（softpub.h：Authenticode 文件/对象信任验证）。</summary>
    private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>单文件验签：S_OK → 签名者名分支；失败 → HRESULT 四分支映射（无效/无签名/验不了）。</summary>
    public static SignatureVerdict Verify(string path)
    {
        var hresult = VerifyTrust(path);
        if (hresult == 0)
        {
            var (status, signerName) = SignatureRules.MapTrustSuccess(TryGetSignerName(path));
            return new SignatureVerdict(status, signerName);
        }
        return new SignatureVerdict(SignatureRules.MapTrustFailure(hresult), null);
    }

    /// <summary>WinVerifyTrust 调用（CsWin32 生成，WINTRUST_DATA/WINTRUST_FILE_INFO 生成布局经实测与 wintrust.h 一致）。</summary>
    private static unsafe int VerifyTrust(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)sizeof(WINTRUST_FILE_INFO),
        };
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)sizeof(WINTRUST_DATA),
            dwUIChoice = WINTRUST_DATA_UICHOICE.WTD_UI_NONE,
            fdwRevocationChecks = WINTRUST_DATA_REVOCATION_CHECKS.WTD_REVOKE_NONE,
            dwUnionChoice = WINTRUST_DATA_UNION_CHOICE.WTD_CHOICE_FILE,
            dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_IGNORE,
        };
        var action = ActionGenericVerifyV2; // 只读字段不可按引用传递，拷贝局部后取址
        fixed (char* pszPath = path)
        {
            fileInfo.pcwszFilePath = pszPath;
            data.Anonymous.pFile = &fileInfo;
            return PInvoke.WinVerifyTrust(default, ref action, &data);
        }
    }

    /// <summary>签名者显示名（SimpleDisplay CN）。验证成功但名不可读（文件竞态消失/形态异常）→ null，映射层保守落 Unverifiable。</summary>
    private static string? TryGetSignerName(string path)
    {
        try
        {
            using var raw = X509Certificate.CreateFromSignedFile(path);
            using var certificate = new X509Certificate2(raw);
            var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
