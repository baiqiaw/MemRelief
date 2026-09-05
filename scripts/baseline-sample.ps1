# MemRelief 本机基线采样脚本（只读；PRD 附录待确认 #2 用，卡顿时段可重跑对照）
$ErrorActionPreference = 'Continue'
$OutputEncoding = [Console]::OutputEncoding

"采样时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
""

"=== [1] 系统内存现状 ==="
$os = Get-CimInstance Win32_OperatingSystem
$totalGB = [math]::Round($os.TotalVisibleMemorySize/1MB, 2)
$freeGB  = [math]::Round($os.FreePhysicalMemory/1MB, 2)
$inUseGB = [math]::Round($totalGB - $freeGB, 2)
"物理内存: 总 $totalGB GB | In Use $inUseGB GB ($([math]::Round($inUseGB/$totalGB*100,1))%) | 可用 $freeGB GB"

$perf = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory
$commitGB = [math]::Round($perf.CommittedBytes/1GB, 2)
$limitGB  = [math]::Round($perf.CommitLimit/1GB, 2)
"Commit: 已提交 $commitGB GB / 上限 $limitGB GB ($([math]::Round($commitGB/$limitGB*100,1))%)"
"Available: $([math]::Round($perf.AvailableBytes/1GB,2)) GB | Standby(约) = Available - FreeAndZero = $([math]::Round(($perf.AvailableBytes - $perf.FreeAndZeroPageListBytes)/1GB,2)) GB"

# 硬缺页速率：两次采样间隔 3s
Start-Sleep -Seconds 3
$perf2 = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory
"硬缺页速率(3s 窗口): PagesInput/sec = $($perf2.PagesInputPersec) | PageReads/sec = $($perf2.PageReadsPersec)  (上一次: $($perf.PagesInputPersec)/$($perf.PageReadsPersec))"
""

"=== [2] 进程快照与孤儿/残留识别（口径同 PRD：PPID 死亡或 PID 复用 → 孤儿；模式库子串匹配）==="
$procs = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name, ExecutablePath, CreationDate
$pmem = @{}
Get-Process | ForEach-Object { $pmem[$_.Id] = $_.PrivateMemorySize64 }

$aliveSet = @{}; $byPid = @{}
foreach ($p in $procs) { $aliveSet[$p.ProcessId] = $true; $byPid[$p.ProcessId] = $p }

$pattern = 'updater|update\.exe|crashpad|crashreporter|setup'

$classified = foreach ($p in $procs) {
    $priv = if ($pmem.ContainsKey([int]$p.ProcessId)) { $pmem[[int]$p.ProcessId] } else { 0 }
    # 系统/保护面排除（对应 PRD 🚫 保护名单口径：无路径或 %windir% 下进程不参与孤儿/残留判定）
    $sys = (-not $p.ExecutablePath) -or ($p.ExecutablePath -like "$env:windir*")
    $isOrphan = $false
    if (-not $sys) {
        if ($p.ParentProcessId -and $p.ParentProcessId -ne 0 -and -not $aliveSet.ContainsKey($p.ParentProcessId)) { $isOrphan = $true }
        elseif ($p.ParentProcessId -and $p.ParentProcessId -ne 0) {
            $parent = $byPid[$p.ParentProcessId]
            if ($parent -and $parent.CreationDate -and $p.CreationDate -and $parent.CreationDate -gt $p.CreationDate) { $isOrphan = $true }
        }
    }
    $isPattern = (-not $sys) -and (($p.Name -match $pattern) -or ($p.ExecutablePath -and $p.ExecutablePath -match $pattern))
    [PSCustomObject]@{
        PID = $p.ProcessId; Name = $p.Name
        PrivateMB = [math]::Round($priv/1MB, 1)
        Orphan = $isOrphan; Pattern = $isPattern
        Path = if ($p.ExecutablePath) { $p.ExecutablePath } else { '' }
    }
}

$targets = $classified | Where-Object { $_.Orphan -or $_.Pattern }
$orphans = $targets | Where-Object Orphan
$patternOnly = $targets | Where-Object { -not $_.Orphan -and $_.Pattern }
$orphanSum = ($orphans | Measure-Object PrivateMB -Sum).Sum
$patternOnlySum = ($patternOnly | Measure-Object PrivateMB -Sum).Sum
if (-not $orphanSum) { $orphanSum = 0 }
if (-not $patternOnlySum) { $patternOnlySum = 0 }
"进程总数: $($classified.Count)"
"孤儿进程: $($orphans.Count) 个 | 聚合私有提交: $([math]::Round($orphanSum,1)) MB"
"模式库命中(非孤儿): $($patternOnly.Count) 个 | 聚合: $([math]::Round($patternOnlySum,1)) MB"
"== 合计(孤儿+残留判定命中, 上界): $([math]::Round($orphanSum + $patternOnlySum,1)) MB =="
""

"=== [3] 判定命中进程 Top 20（按私有提交降序）==="
if ($targets) { $targets | Sort-Object PrivateMB -Descending | Select-Object -First 20 | Format-Table PID, Name, PrivateMB, Orphan, Pattern, Path -AutoSize | Out-String -Width 260 }
else { "（无命中）" }
""
"=== [4] 全体进程 Top 10（对照：当前内存大头是谁）==="
$classified | Sort-Object PrivateMB -Descending | Select-Object -First 10 | Format-Table PID, Name, PrivateMB, Orphan, Pattern -AutoSize | Out-String -Width 160
