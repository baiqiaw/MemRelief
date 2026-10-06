# MemRelief 质量门禁入口：编译 → Core 引用门禁（法-1）→ 测试+覆盖率阈值（Core/App/验证台三测试项目，≥80%，total line 口径）
# 用法: pwsh scripts/gate.ps1
# 任一环节失败即非零退出（fail-fast），供日常与验收前一键自检。
# 覆盖率阈值在此注入（/p: 传 coverlet.msbuild）：日常直接 dotnet test 仍出覆盖率报告但不做阈值强制，
# 局部/单测运行不被全量阈值误伤。
# 日志落盘（#62）：每次运行全量双写 logs/gate/（保留最近 5 份），与调用方采证管道解耦——
# 采证管道截断曾丢失失败运行的测试摘要与覆盖率表，复发时凭全量日志归因（重试/阈值带宽待证实②后再议）。
# 实现注：Start-Transcript 实测不捕获重定向下原生命令输出，故逐命令 Tee-Object 双写。
$ErrorActionPreference = 'Continue'
# 门禁结论常经管道/重定向采集（issue 证据），固定 UTF-8 输出
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$repo = Join-Path $PSScriptRoot '..'

$logDir = Join-Path $repo 'logs\gate'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logPath = Join-Path $logDir ("gate-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
# 开跑前清旧：保留最近 4 份，加本次共 ≤5（文件名含时间戳，字典序即时间序）
Get-ChildItem $logDir -Filter 'gate-*.log' | Sort-Object Name -Descending | Select-Object -Skip 4 | Remove-Item -Force

# 双写：控制台（保留原色）+ 日志文件（复发时的诊断起点）
function Write-Log([string]$Message, [string]$Color = 'Gray') {
    Write-Host $Message -ForegroundColor $Color
    Add-Content -Path $logPath -Value $Message
}
Write-Log ("=== gate 全链开始 {0:yyyy-MM-dd HH:mm:ss} ===" -f (Get-Date))

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Log '[gate] FAIL：dotnet 不在 PATH' 'Red'
    exit 1
}

Write-Log '=== 门禁 1/5：编译解决方案 ===' 'Cyan'
dotnet build (Join-Path $repo 'MemRelief.sln') -c Debug --nologo -v q 2>&1 | Tee-Object -FilePath $logPath -Append
if ($LASTEXITCODE -ne 0) { Write-Log "[gate] 编译失败（全量日志：$logPath）" 'Red'; exit 1 }

Write-Log '=== 门禁 2/5：Core 引用检查（法-1） ===' 'Cyan'
# 同进程直调（少付一次 pwsh 冷启动；脚本能跑到这行即 pwsh 在位）
# 6>&1：引用门禁输出全走 Write-Host（信息流），不并流则日志门禁 2 段为空、失败明细恰不落盘（cross-review #62 实测）
& (Join-Path $PSScriptRoot 'check-core-refs.ps1') 2>&1 6>&1 | Tee-Object -FilePath $logPath -Append
if ($LASTEXITCODE -ne 0) { Write-Log "[gate] 引用门禁失败（全量日志：$logPath）" 'Red'; exit 1 }

Write-Log '=== 门禁 3/5：Core 测试 + 覆盖率 ≥80% ===' 'Cyan'
dotnet test (Join-Path $repo 'tests\MemRelief.Core.Tests') -c Debug --nologo `
    -p:CollectCoverage=true "-p:Include=[MemRelief.Core]*" `
    -p:Threshold=80 -p:ThresholdType=line -p:ThresholdStat=total 2>&1 | Tee-Object -FilePath $logPath -Append
if ($LASTEXITCODE -ne 0) { Write-Log "[gate] Core 测试/覆盖率门禁失败（全量日志：$logPath）" 'Red'; exit 1 }

# App 层（T-14 起纳入机器门）：阈值同 80（total line），Include/Exclude 由 App.Tests.csproj 承载
# （统计 MemRelief.App 手写类型，XAML 生成代码排除口径见该 csproj 注释）
Write-Log '=== 门禁 4/5：App 测试 + 覆盖率 ≥80% ===' 'Cyan'
dotnet test (Join-Path $repo 'tests\MemRelief.App.Tests') -c Debug --nologo `
    -p:Threshold=80 -p:ThresholdType=line -p:ThresholdStat=total 2>&1 | Tee-Object -FilePath $logPath -Append
if ($LASTEXITCODE -ne 0) { Write-Log "[gate] App 测试/覆盖率门禁失败（全量日志：$logPath）" 'Red'; exit 1 }

# 验证台（T-21 起纳入机器门）：阈值同 80（total line），Include 由本命令注入（只统计 MemRelief.Bench）
Write-Log '=== 门禁 5/5：验证台测试 + 覆盖率 ≥80% ===' 'Cyan'
dotnet test (Join-Path $repo 'tests\MemRelief.Bench.Tests') -c Debug --nologo `
    -p:CollectCoverage=true "-p:Include=[MemRelief.Bench]*" `
    -p:Threshold=80 -p:ThresholdType=line -p:ThresholdStat=total 2>&1 | Tee-Object -FilePath $logPath -Append
if ($LASTEXITCODE -ne 0) { Write-Log "[gate] 验证台测试/覆盖率门禁失败（全量日志：$logPath）" 'Red'; exit 1 }

Write-Log '[gate] 全链通过' 'Green'
exit 0
