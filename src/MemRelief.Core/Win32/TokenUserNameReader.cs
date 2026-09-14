using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace MemRelief.Core.Win32;

/// <summary>
/// 进程令牌所有者裸名读取共享核心（T-01 裁决①格式口径）：OpenProcessToken(TOKEN_QUERY) →
/// 两段式 GetTokenInformation(TokenUser) → LookupAccountSid lpName（裸名，不含域前缀）。
/// 消费方：Scanner.NativeProcessEnumerator（口径 #2 采集侧）/ Releaser.Win32ProcessChannel（T-10 执行期二分），
/// 禁止第二处 inline 实现（issue #39 收口）。
/// </summary>
/// <remarks>覆盖率豁免（ExcludeFromCodeCoverage）：纯 Win32 互操作薄通道；真机路径由扫描/释放集成测试覆盖。</remarks>
[ExcludeFromCodeCoverage]
internal static unsafe class TokenUserNameReader
{
    private const uint MaxTokenBufferLength = 4096;

    /// <summary>成功 → (裸名, null)；任一步失败 → (null, 失败原因)。不抛出不重试。</summary>
    public static (string? Name, string? FailReason) TryReadFromProcess(HANDLE processHandle)
    {
        try
        {
            HANDLE token = default;
            if (!PInvoke.OpenProcessToken(processHandle, TOKEN_ACCESS_MASK.TOKEN_QUERY, &token))
            {
                return (null, $"令牌不可读（Win32 错误 {Marshal.GetLastWin32Error()}）");
            }
            try
            {
                return ReadFromToken(token);
            }
            finally
            {
                PInvoke.CloseHandle(token);
            }
        }
        catch (Exception ex)
        {
            return (null, $"意外异常 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static (string? Name, string? FailReason) ReadFromToken(HANDLE token)
    {
        // 两段式：首调探测长度——预期返回 false（ERROR_INSUFFICIENT_BUFFER=122）并回填 returnLength，非失败
        uint returnLength = 0;
        _ = PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, null, 0, &returnLength);
        if (returnLength == 0)
        {
            return (null, $"令牌信息不可读（Win32 错误 {Marshal.GetLastWin32Error()}）");
        }
        if (returnLength > MaxTokenBufferLength)
        {
            return (null, "TokenUser 缓冲超长");
        }

        Span<byte> buffer = stackalloc byte[(int)returnLength];
        fixed (byte* bufferPtr = buffer)
        {
            if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, bufferPtr, returnLength, &returnLength))
            {
                return (null, $"令牌信息不可读（Win32 错误 {Marshal.GetLastWin32Error()}）");
            }

            // TOKEN_USER.User 为 SID_AND_ATTRIBUTES（PSID 指针+属性），SID 体在缓冲区内部
            var tokenUser = (TOKEN_USER*)bufferPtr;
            if (tokenUser->User.Sid.Value == null)
            {
                return (null, "令牌无用户 SID");
            }
            Span<char> name = stackalloc char[256];
            Span<char> domain = stackalloc char[256];
            uint nameLen = (uint)name.Length;
            uint domainLen = (uint)domain.Length;
            fixed (char* namePtr = name)
            fixed (char* domainPtr = domain)
            {
                // peUse 不可传空（部分系统路径无条件写入，空指针=进程内 AV）
                SID_NAME_USE sidUse = default;
                if (!PInvoke.LookupAccountSid(default, tokenUser->User.Sid, new PWSTR(namePtr), &nameLen, new PWSTR(domainPtr), &domainLen, &sidUse))
                {
                    return (null, $"账户名解析失败（Win32 错误 {Marshal.GetLastWin32Error()}）");
                }
                // 裸所有者名（lpName 分量，不含域前缀）——裁决①
                return (new string(namePtr, 0, (int)nameLen), null);
            }
        }
    }
}
