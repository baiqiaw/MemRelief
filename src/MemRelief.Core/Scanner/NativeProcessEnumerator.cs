using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.ProcessStatus;
using Windows.Win32.System.Threading;

namespace MemRelief.Core.Scanner;

/// <summary>
/// 原生进程枚举（口径表 #1/#13 数据侧通道）：Toolhelp32 全量枚举 + 逐 pid 句柄级字段采集。
/// 薄通道层：仅做 Win32 错误码→outcome 的机械翻译与防御性字段捕获（异常降级为字段级失败），
/// 失败语义与判定映射归 SnapshotAssembler。
/// 通道出处：PID/PPID=Toolhelp32、创建时间=GetProcessTimes、私有提交=GetProcessMemoryInfo(PrivateUsage)
/// 三项按 PRD 口径表钉死；路径（QueryFullProcessImageName）/所有者（OpenProcessToken→LookupAccountSid，
/// 裸用户名，data-contracts §2 T-01 裁决①）为实现选型。
/// CsWin32 blittable 签名（NativeMethods.json allowMarshaling=false），HANDLE 手工关闭。
/// </summary>
/// <remarks>
/// 覆盖率豁免（ExcludeFromCodeCoverage）：纯 Win32 互操作适配层，无判定逻辑（错误码机械翻译 +
/// 字段级异常捕获降级）；正常路径由真机集成冒烟（ScannerIntegrationTests）实跑验证。
/// 判定与映射逻辑全数位于纯函数 SnapshotAssembler（全量单测覆盖）。
/// 决策依据见 system-spec §7 变更记录 2026-09-07 与 T-01 issue 证据评论。
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class NativeProcessEnumerator
{
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>枚举全量进程行；TakenAtUtc=Toolhelp 快照句柄创建时点（契约"一次扫描的一致视图"）。</summary>
    /// <exception cref="InvalidOperationException">快照创建重试仍失败，或迭代异常终止（如 ERROR_PARTIAL_COPY）——扫描失败，不产出残缺快照。</exception>
    public (List<RawProcess> Rows, DateTime TakenAtUtc) Enumerate()
    {
        for (var attempt = 0; ; attempt++)
        {
            var snapshot = PInvoke.CreateToolhelp32Snapshot(CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
            if (snapshot == default)
            {
                if (attempt > 0)
                {
                    Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
                }
                continue;
            }

            try
            {
                // 时点锚定在快照句柄创建成功一刻，先于逐条读取
                var takenAtUtc = DateTime.UtcNow;
                return (ReadSnapshot(snapshot), takenAtUtc);
            }
            finally
            {
                PInvoke.CloseHandle(snapshot);
            }
        }
    }

    private static List<RawProcess> ReadSnapshot(HANDLE snapshot)
    {
        var rows = new List<RawProcess>(512);
        unsafe
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
            // 首条失败=枚举通道损坏（健康系统恒有 pid 0），抛异常交编排方转 ScanFailed，禁止伪装成空快照
            if (!PInvoke.Process32FirstW(snapshot, &entry))
            {
                throw new InvalidOperationException($"进程枚举失败（Process32FirstW，Win32 错误 {Marshal.GetLastWin32Error()}）");
            }
            while (true)
            {
                var name = ReadEntryName(ref entry);
                rows.Add(ReadProcessFields((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, name));
                if (PInvoke.Process32NextW(snapshot, &entry))
                {
                    continue;
                }
                var error = Marshal.GetLastWin32Error();
                if (error is ErrorNoMoreFiles or 0)
                {
                    return rows; // 正常终止
                }
                // 迭代中断（如 299 ERROR_PARTIAL_COPY，系统繁忙下 Toolhelp 已知失败）：残缺快照=假阴性，宁可扫描失败
                throw new InvalidOperationException($"进程枚举中断（Process32NextW，Win32 错误 {error}），不产出残缺快照");
            }
        }
    }

    private static unsafe string ReadEntryName(ref PROCESSENTRY32W entry)
    {
        // 定容 260 内联数组，不依赖 0 终止假设：有界扫描至首个 \0
        fixed (char* p = &entry.szExeFile[0])
        {
            var length = 0;
            while (length < 260 && p[length] != '\0')
            {
                length++;
            }
            return new string(p, 0, length);
        }
    }

    /// <summary>
    /// 口径 #7 差分终点二次采样（T-02，grilling 裁决②：窗口=TakeSnapshot 采集段）：对已采集起点的存活进程重开句柄取
    /// kernel+user 合计，差分秒输出；窗口内退出（打开失败）→ null（合并层配 SignalFailure #7）。幂等只读、无副作用。
    /// </summary>
    public Dictionary<int, double?> ReadCpuDeltas(IReadOnlyList<RawProcess> rows)
    {
        var deltas = new Dictionary<int, double?>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Open != ProcessOpenOutcome.Opened || !row.CpuStart.IsOk || row.CpuStart.Value is not { } start)
            {
                deltas[row.Pid] = null;   // 起点不可得（打开受拒等，T-01 已有失败记录）
                continue;
            }

            var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)row.Pid);
            if (handle == default)
            {
                deltas[row.Pid] = null;   // 窗口内退出/受拒 → 差分不可得（装配层配 SignalFailure #7）
                continue;
            }

            try
            {
                var end = TryGetTotalCpu(handle);
                // pid 复用防御（T-02 cross-review F3）：终点 < 起点=句柄指向复用新进程，差分无意义 → null（配 SignalFailure #7，与"不可得"同向）
                deltas[row.Pid] = end is { } total && total >= start ? total - start : null;
            }
            finally
            {
                PInvoke.CloseHandle(handle);
            }
        }
        return deltas;
    }

    /// <summary>kernel+user 合计秒；读取失败 → null。</summary>
    private static unsafe double? TryGetTotalCpu(HANDLE handle)
    {
        var creation = default(System.Runtime.InteropServices.ComTypes.FILETIME);
        var exit = default(System.Runtime.InteropServices.ComTypes.FILETIME);
        var kernel = default(System.Runtime.InteropServices.ComTypes.FILETIME);
        var user = default(System.Runtime.InteropServices.ComTypes.FILETIME);
        if (!PInvoke.GetProcessTimes(handle, &creation, &exit, &kernel, &user))
        {
            return null;
        }
        var kernelTicks = ((ulong)kernel.dwHighDateTime << 32) | (uint)kernel.dwLowDateTime;
        var userTicks = ((ulong)user.dwHighDateTime << 32) | (uint)user.dwLowDateTime;
        return (kernelTicks + userTicks) / 10_000_000.0;
    }

    private static RawProcess ReadProcessFields(int pid, int ppid, string name)
    {
        var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == default)
        {
            // 进程级打开失败：被拒→AccessDenied（🚫 通道）；其余（pid 失效/系统进程不可打开）→Vanished（裁决③④）
            var error = Marshal.GetLastWin32Error();
            var open = error == 5 /* ERROR_ACCESS_DENIED */ ? ProcessOpenOutcome.AccessDenied : ProcessOpenOutcome.Vanished;
            return FailRow(pid, ppid, name, open, error);
        }

        try
        {
            var (creation, cpuStart) = TryGetCreationAndCpu(handle);
            return new RawProcess(
                pid,
                ppid,
                name,
                ProcessOpenOutcome.Opened,
                TryGetPath(handle),
                creation,
                TryGetPrivateCommit(handle),
                TryGetOwnerUser(handle),
                CpuStart: cpuStart,
                CommandLine: null);
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }

    private static RawProcess FailRow(int pid, int ppid, string name, ProcessOpenOutcome open, int error)
    {
        // Error 仅承载机械错误码；判定语义文案归 SnapshotAssembler（装配层 Detail 为唯一叙事口径）
        var reason = $"Win32 错误 {error}";
        return new RawProcess(
            pid, ppid, name, open,
            ProcessField<string?>.Fail(reason),
            ProcessField<DateTime?>.Fail(reason),
            ProcessField<long?>.Fail(reason),
            ProcessField<string?>.Fail(reason),
            CpuStart: ProcessField<double?>.Fail(reason),
            CommandLine: null);
    }

    private static unsafe ProcessField<string?> TryGetPath(HANDLE handle)
    {
        try
        {
            Span<char> buffer = stackalloc char[1024];
            fixed (char* p = buffer)
            {
                var size = (uint)buffer.Length;
                if (PInvoke.QueryFullProcessImageName(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, new PWSTR(p), &size))
                {
                    return ProcessField<string?>.Ok(new string(p, 0, (int)size));
                }
                // 长路径（LongPaths 开启）场景：API 回填所需尺寸时按其重取一次（上限 32K，NT 路径上限）
                var needed = size;
                if (needed is > 1024 and <= 32768)
                {
                    var extended = new char[needed];
                    fixed (char* ep = extended)
                    {
                        var extendedSize = (uint)extended.Length;
                        if (PInvoke.QueryFullProcessImageName(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, new PWSTR(ep), &extendedSize))
                        {
                            return ProcessField<string?>.Ok(new string(ep, 0, (int)extendedSize));
                        }
                    }
                }
            }
            return ProcessField<string?>.Fail($"Win32 错误 {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            return ProcessField<string?>.Fail($"意外异常 {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>创建时间 + CPU 差分起点（口径 #7）：同一 GetProcessTimes 调用取四时间，kernel+user 合成为秒。</summary>
    private static unsafe (ProcessField<DateTime?> Creation, ProcessField<double?> CpuStart) TryGetCreationAndCpu(HANDLE handle)
    {
        try
        {
            var creation = default(System.Runtime.InteropServices.ComTypes.FILETIME);
            var exit = default(System.Runtime.InteropServices.ComTypes.FILETIME);
            var kernel = default(System.Runtime.InteropServices.ComTypes.FILETIME);
            var user = default(System.Runtime.InteropServices.ComTypes.FILETIME);
            if (!PInvoke.GetProcessTimes(handle, &creation, &exit, &kernel, &user))
            {
                var reason = $"Win32 错误 {Marshal.GetLastWin32Error()}";
                return (ProcessField<DateTime?>.Fail(reason), ProcessField<double?>.Fail(reason));
            }
            // 高低位在 ulong 域合成（FILETIME 字段为 int，long|uint 组合会符号扩展低 DWORD 产生假负值，CS0675）
            var fileTime = (long)(((ulong)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime);
            // 负值=1601 前不存在的时间（个别进程可报），按不可读处理，哨兵+失败记录由装配层兜住
            if (fileTime < 0)
            {
                return (
                    ProcessField<DateTime?>.Fail($"创建时间值非法（fileTime={fileTime}）"),
                    ProcessField<double?>.Fail("CPU 起点随创建时间同调用采集失败"));
            }
            // CPU 起点（口径 #7，T-02）：kernel/user 为 100ns FILETIME 域，合计换算秒（ulong 域合成防符号扩展）
            var kernelTicks = ((ulong)kernel.dwHighDateTime << 32) | (uint)kernel.dwLowDateTime;
            var userTicks = ((ulong)user.dwHighDateTime << 32) | (uint)user.dwLowDateTime;
            var cpuStart = (kernelTicks + userTicks) / 10_000_000.0;
            return (ProcessField<DateTime?>.Ok(DateTime.FromFileTimeUtc(fileTime)), ProcessField<double?>.Ok(cpuStart));
        }
        catch (Exception ex)
        {
            // 字段级意外异常降级为该字段失败，不击穿整次扫描（历史 0xC0000005 同层教训）
            var reason = $"意外异常 {ex.GetType().Name}: {ex.Message}";
            return (ProcessField<DateTime?>.Fail(reason), ProcessField<double?>.Fail(reason));
        }
    }

    private static unsafe ProcessField<long?> TryGetPrivateCommit(HANDLE handle)
    {
        try
        {
            // 以 EX 扩展结构承载，按基类指针传入（cb=EX 尺寸，PrivateUsage=私有提交，口径#13）；栈上局部无需 fixed
            var counters = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)sizeof(PROCESS_MEMORY_COUNTERS_EX) };
            var p = &counters;
            if (!PInvoke.GetProcessMemoryInfo(handle, (PROCESS_MEMORY_COUNTERS*)p, counters.cb))
            {
                return ProcessField<long?>.Fail($"Win32 错误 {Marshal.GetLastWin32Error()}");
            }
            return ProcessField<long?>.Ok((long)counters.PrivateUsage);
        }
        catch (Exception ex)
        {
            return ProcessField<long?>.Fail($"意外异常 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static unsafe ProcessField<string?> TryGetOwnerUser(HANDLE handle)
    {
        try
        {
            HANDLE token = default;
            if (!PInvoke.OpenProcessToken(handle, TOKEN_ACCESS_MASK.TOKEN_QUERY, &token))
            {
                return ProcessField<string?>.Fail($"令牌不可读（Win32 错误 {Marshal.GetLastWin32Error()}）");
            }
            try
            {
                return ReadTokenUserName(token);
            }
            finally
            {
                PInvoke.CloseHandle(token);
            }
        }
        catch (Exception ex)
        {
            return ProcessField<string?>.Fail($"意外异常 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static unsafe ProcessField<string?> ReadTokenUserName(HANDLE token)
    {
        // 两段式：首调探测长度——预期返回 false（ERROR_INSUFFICIENT_BUFFER=122）并回填 returnLength，非失败
        uint returnLength = 0;
        _ = PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, null, 0, &returnLength);
        if (returnLength == 0)
        {
            return ProcessField<string?>.Fail($"令牌信息不可读（Win32 错误 {Marshal.GetLastWin32Error()}）");
        }
        if (returnLength > 4096)
        {
            return ProcessField<string?>.Fail("TokenUser 缓冲超长");
        }

        Span<byte> tokenBuffer = stackalloc byte[(int)returnLength];
        fixed (byte* bufferPtr = tokenBuffer)
        {
            if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, bufferPtr, returnLength, &returnLength))
            {
                return ProcessField<string?>.Fail($"令牌信息不可读（Win32 错误 {Marshal.GetLastWin32Error()}）");
            }

            // TOKEN_USER.User 为 SID_AND_ATTRIBUTES（PSID 指针+属性），SID 体在缓冲区内部
            var tokenUser = (TOKEN_USER*)bufferPtr;
            if (tokenUser->User.Sid.Value == null)
            {
                return ProcessField<string?>.Fail("令牌无用户 SID");
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
                    return ProcessField<string?>.Fail($"账户名解析失败（Win32 错误 {Marshal.GetLastWin32Error()}）");
                }
                // 裸所有者名（lpName 分量，不含域前缀）——裁决①
                return ProcessField<string?>.Ok(new string(namePtr, 0, (int)nameLen));
            }
        }
    }
}
