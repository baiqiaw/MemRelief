using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using MemRelief.Core.Contracts;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace MemRelief.Core.Releaser;

/// <summary>
/// 执行期进程通道工厂（T-09）：打开存活进程并承载两段式结束的机械通道。
/// 打开权限 = TERMINATE | QUERY_LIMITED_INFORMATION | SYNCHRONIZE 一次取足
/// （身份校验读 GetProcessTimes、等待 WaitForSingleObject、强杀 TerminateProcess 同一句柄承载，
/// 关闭句柄经 Dispose 统一）；打开失败按错误码二分：5=受拒（Denied），其余=已消失（Vanished）。
/// </summary>
/// <remarks>
/// 覆盖率豁免（ExcludeFromCodeCoverage）：纯 Win32 互操作薄通道（错误码机械翻译），无判定逻辑；
/// 分支全量由合成单测以假实现驱动（ProcessReleaserTests），正常路径由 TestProcs 真机集成实跑。
/// </remarks>
[ExcludeFromCodeCoverage]
internal sealed class Win32ProcessChannel : ILiveProcessOpener
{
    public ILiveProcess? TryOpen(int pid, out LiveOpenKind kind, out int win32Error)
    {
        var handle = PInvoke.OpenProcess(
            PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE
            | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION
            | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE,
            false,
            (uint)pid);
        if (handle == default)
        {
            win32Error = Marshal.GetLastWin32Error();
            kind = win32Error == Win32Errors.ErrorAccessDenied ? LiveOpenKind.Denied : LiveOpenKind.Vanished;
            return null;
        }

        win32Error = 0;
        kind = LiveOpenKind.Opened;
        return new Win32LiveProcess(pid, handle);
    }
}

/// <summary>单进程执行会话：身份校验 → 窗口自查 → WM_CLOSE → 等待 → 强杀的句柄域机械翻译。</summary>
[ExcludeFromCodeCoverage]
internal sealed class Win32LiveProcess : ILiveProcess
{
    // WM_CLOSE=0x0010（winuser.h 稳定消息常量）本地钉死：CsWin32 0.3.333 常量清单对本项实测不生成
    // （NativeMethods.txt 含/不含该条均无生成物，cross-review 实证），不属函数例外清单管辖
    private const uint WmClose = 0x0010;

    private HANDLE _handle;

    internal Win32LiveProcess(int pid, HANDLE handle)
    {
        Pid = pid;
        _handle = handle;
    }

    public int Pid { get; }

    public bool IdentityMatches(ProcessSnapshot snapshot)
    {
        // 快照创建时间哨兵（采集不可读，T-01 裁决②）不参与比较：不可判 = 不可杀（fail-closed）
        if (snapshot.CreationTimeUtc == DateTime.MinValue)
        {
            return false;
        }

        var creation = default(System.Runtime.InteropServices.ComTypes.FILETIME);
        var exit = default(System.Runtime.InteropServices.ComTypes.FILETIME);
        var kernel = default(System.Runtime.InteropServices.ComTypes.FILETIME);
        var user = default(System.Runtime.InteropServices.ComTypes.FILETIME);
        unsafe
        {
            if (!PInvoke.GetProcessTimes(_handle, &creation, &exit, &kernel, &user))
            {
                return false;   // 存活侧创建时间不可读：身份不可判
            }
        }

        var fileTime = (long)(((ulong)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime);
        if (fileTime < 0 || DateTime.FromFileTimeUtc(fileTime) != snapshot.CreationTimeUtc)
        {
            return false;
        }

        var liveName = TryGetImageName();
        return liveName is not null
            && string.Equals(liveName, snapshot.Name, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<nint>? CollectTopLevelWindows() => ExecutionWindowProbe.TryCollectTopLevelWindows(Pid);

    public void PostClose(nint hwnd)
    {
        // 投递失败（窗口已销毁等）不重试：等待阶段的退出探测自然收口
        _ = PInvoke.PostMessage((HWND)hwnd, WmClose, default, default);
    }

    public bool WaitExit(int milliseconds)
    {
        var result = PInvoke.WaitForSingleObject(_handle, (uint)Math.Clamp(milliseconds, 0, int.MaxValue));
        // WAIT_OBJECT_0=已退出；WAIT_FAILED=句柄失效（进程对象已不在观察域，按退出收口）；超时=仍存活
        return result != WAIT_EVENT.WAIT_TIMEOUT;
    }

    public int? Terminate()
    {
        unsafe
        {
            if (PInvoke.TerminateProcess(_handle, 1))
            {
                return null;
            }
        }
        return Marshal.GetLastWin32Error();
    }

    public unsafe string? TryGetTokenUserName()
    {
        // 执行期令牌所有者（T-10 权限二分的"令牌"源）：与 scanner 采集侧同款两段式读取
        //（OpenProcessToken(TOKEN_QUERY) → GetTokenInformation(TokenUser) → LookupAccountSid 裸名，
        // T-01 裁决①格式口径）。任一步失败 → null（上层回退快照 OwnerUser），不抛出不重试。
        try
        {
            HANDLE token = default;
            if (!PInvoke.OpenProcessToken(_handle, TOKEN_ACCESS_MASK.TOKEN_QUERY, &token))
            {
                return null;
            }

            try
            {
                // 两段式：首调探测长度——预期返回 false（ERROR_INSUFFICIENT_BUFFER=122）并回填 returnLength
                uint returnLength = 0;
                _ = PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, null, 0, &returnLength);
                if (returnLength == 0 || returnLength > 4096)
                {
                    return null;
                }

                Span<byte> buffer = stackalloc byte[(int)returnLength];
                fixed (byte* bufferPtr = buffer)
                {
                    if (!PInvoke.GetTokenInformation(
                            token, TOKEN_INFORMATION_CLASS.TokenUser, bufferPtr, returnLength, &returnLength))
                    {
                        return null;
                    }

                    var tokenUser = (TOKEN_USER*)bufferPtr;
                    if (tokenUser->User.Sid.Value == null)
                    {
                        return null;
                    }

                    Span<char> name = stackalloc char[256];
                    Span<char> domain = stackalloc char[256];
                    uint nameLen = (uint)name.Length;
                    uint domainLen = (uint)domain.Length;
                    fixed (char* namePtr = name)
                    fixed (char* domainPtr = domain)
                    {
                        // peUse 不可传空（部分系统路径无条件写入，空指针=进程内 AV；scanner 同款）
                        SID_NAME_USE sidUse = default;
                        if (!PInvoke.LookupAccountSid(
                                default, tokenUser->User.Sid, new PWSTR(namePtr), &nameLen,
                                new PWSTR(domainPtr), &domainLen, &sidUse))
                        {
                            return null;
                        }

                        return new string(namePtr, 0, (int)nameLen);
                    }
                }
            }
            finally
            {
                PInvoke.CloseHandle(token);
            }
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        var handle = _handle;
        _handle = default;
        if (handle != default)
        {
            PInvoke.CloseHandle(handle);
        }
    }

    private unsafe string? TryGetImageName()
    {
        try
        {
            Span<char> buffer = stackalloc char[1024];
            fixed (char* p = buffer)
            {
                var size = (uint)buffer.Length;
                if (PInvoke.QueryFullProcessImageName(
                        _handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, new Windows.Win32.Foundation.PWSTR(p), &size))
                {
                    return Path.GetFileName(new string(p, 0, (int)size));
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
