using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MemRelief.Core.Scanner;

/// <summary>
/// 启动文件夹通道（口径 #12，WBS T-03）：用户（shell:startup）+ 公共（shell:common startup）两处；
/// .lnk 经 IShellLinkW 解析目标可执行路径（8.3 短名展开），.exe 以文件自身为路径，其余扩展名不采集；
/// StartupApproved\StartupFolder（HKCU+HKLM）读禁用态并滤除禁用条目（值名=快捷方式文件名，
/// 含/不带 .lnk 扩展名两种存放形态均比对）。
/// </summary>
/// <remarks>
/// 技术通道：IShellLinkW 手写 COM 接口（shell32 内建，CLSID/接口 GUID 为 shell 标准；IPersistFile 复用
/// BCL System.Runtime.InteropServices.ComTypes）；单条目解析失败仅跳过该条目，目录级异常 → false
/// （上层配 SignalFailure #12）。
/// 覆盖率豁免（ExcludeFromCodeCoverage）：COM/文件系统互操作薄通道，无判定逻辑；禁用态读取归
/// StartupApprovedReader（共用单点）、字节判读归 SignalRules.IsDisabledApprovedValue（纯函数，单测承载）；
/// 正常路径真机集成冒烟实跑。
/// </remarks>
[ExcludeFromCodeCoverage]
internal static class StartupFolderSourceProbe
{
    private const int StgmRead = 0;   // STGM_READ

    /// <summary>目录/注册表读取异常 → false（上层按口径 #12 兜底）；目录不存在 = 空集非失败。</summary>
    public static bool TryCollect(out IReadOnlyList<StartupFolderSource> entries)
    {
        List<StartupFolderSource> collected;
        try
        {
            var hkcuDisabled = StartupApprovedReader.ReadDisabledNames(Registry.CurrentUser, "StartupFolder");
            var hklmDisabled = StartupApprovedReader.ReadDisabledNames(Registry.LocalMachine, "StartupFolder");
            var disabledNames = new HashSet<string>(hkcuDisabled.Concat(hklmDisabled), StringComparer.OrdinalIgnoreCase);
            collected = new List<StartupFolderSource>();
            CollectFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), disabledNames, collected);
            CollectFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), disabledNames, collected);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException
                                   or COMException)
        {
            entries = new List<StartupFolderSource>();
            return false;
        }

        entries = collected;
        return true;
    }

    private static void CollectFolder(string folder, ISet<string> disabledNames, List<StartupFolderSource> collected)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var extension = Path.GetExtension(file);
            var target = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                ? ResolveLinkTarget(file)
                : extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ? ShortPathExpander.Expand(file) : null;
            if (target is null)
            {
                continue;
            }
            var entryName = Path.GetFileName(file);
            if (disabledNames.Contains(entryName) || disabledNames.Contains(Path.GetFileNameWithoutExtension(entryName)))
            {
                continue;   // StartupApproved 禁用态：条目非有效自启动来源
            }
            collected.Add(new StartupFolderSource(entryName, target));
        }
    }

    /// <summary>IShellLinkW 解析 .lnk 目标路径（HRESULT 非成功/空目标返回 null）；单个损坏/非文件目标
    /// 跳过不击穿通道。internal 供真机测试实构造 .lnk 验证读链（本机启动文件夹为空时通道无自然数据）。</summary>
    internal static string? ResolveLinkTarget(string linkPath)
    {
        try
        {
            var shellLinkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));   // CLSID_ShellLink
            if (shellLinkType is null)
            {
                return null;
            }
            var instance = Activator.CreateInstance(shellLinkType);
            if (instance is null)
            {
                return null;
            }
            try
            {
                var shellLink = (IShellLinkW)instance;
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)shellLink).Load(linkPath, StgmRead);
                var buffer = new System.Text.StringBuilder(1024);
                var hr = shellLink.GetPath(buffer, buffer.Capacity, pfd: nint.Zero, fFlags: 0);
                if (hr != 0)
                {
                    return null;   // HRESULT 失败（含路径截断形态）：不采半截路径
                }
                var target = buffer.ToString().Trim();
                return target.Length == 0 ? null : ShortPathExpander.Expand(target);
            }
            finally
            {
                Marshal.ReleaseComObject(instance);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException)
        {
            return null;
        }
    }

    /// <summary>IShellLinkW（shell 标准 vtable 序全成员声明；调用仅用 GetPath，pfd 传 nint.Zero 免 WIN32_FIND_DATA 结构）。</summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath(
            [Out][MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile,
            int cchMaxPath,
            nint pfd,
            uint fFlags);

        [PreserveSig]
        int GetIDList(out nint ppidl);

        [PreserveSig]
        int SetIDList(nint pidl);

        [PreserveSig]
        int GetDescription(
            [Out][MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName,
            int cchMaxName);

        [PreserveSig]
        int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        [PreserveSig]
        int GetWorkingDirectory(
            [Out][MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir,
            int cchMaxPath);

        [PreserveSig]
        int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        [PreserveSig]
        int GetArguments(
            [Out][MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs,
            int cchMaxArgs);

        [PreserveSig]
        int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        [PreserveSig]
        int GetHotkey(out short pwHotkey);

        [PreserveSig]
        int SetHotkey(short wHotkey);

        [PreserveSig]
        int GetShowCmd(out int piShowCmd);

        [PreserveSig]
        int SetShowCmd(int iShowCmd);

        [PreserveSig]
        int GetIconLocation(
            [Out][MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath,
            int cchIconPath,
            out int piIcon);

        [PreserveSig]
        int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        [PreserveSig]
        int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        [PreserveSig]
        int Resolve(nint hwnd, uint fFlags);

        [PreserveSig]
        int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
