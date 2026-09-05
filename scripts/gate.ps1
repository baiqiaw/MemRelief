# MemRelief 质量门禁入口：编译 → Core 引用门禁（法-1）→ 测试+覆盖率阈值（≥80%，全量 total 口径）
# 用法: pwsh scripts/gate.ps1
# 任一环节失败即非零退出（fail-fast），供日常与验收前一键自检。
# 覆盖率阈值在此注入（/p: 传 coverlet.msbuild）：日常直接 dotnet test 仍出覆盖率报告但不做阈值强制，
# 局部/单测运行不被全量阈值误伤。
$ErrorActionPreference = 'Continue'
# 门禁结论常经管道/重定向采集（issue 证据），固定 UTF-8 输出
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$repo = Join-Path $PSScriptRoot '..'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host '[gate] FAIL：dotnet 不在 PATH' -ForegroundColor Red
    exit 1
}

Write-Host '=== 门禁 1/3：编译解决方案 ===' -ForegroundColor Cyan
dotnet build (Join-Path $repo 'MemRelief.sln') -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host '[gate] 编译失败' -ForegroundColor Red; exit 1 }

Write-Host '=== 门禁 2/3：Core 引用检查（法-1） ===' -ForegroundColor Cyan
# 同进程直调（少付一次 pwsh 冷启动；脚本能跑到这行即 pwsh 在位）
& (Join-Path $PSScriptRoot 'check-core-refs.ps1')
if ($LASTEXITCODE -ne 0) { Write-Host '[gate] 引用门禁失败' -ForegroundColor Red; exit 1 }

Write-Host '=== 门禁 3/3：测试 + Core 覆盖率 ≥80% ===' -ForegroundColor Cyan
dotnet test (Join-Path $repo 'tests\MemRelief.Core.Tests') -c Debug --nologo `
    -p:CollectCoverage=true "-p:Include=[MemRelief.Core]*" `
    -p:Threshold=80 -p:ThresholdType=line -p:ThresholdStat=total
if ($LASTEXITCODE -ne 0) { Write-Host '[gate] 测试/覆盖率门禁失败' -ForegroundColor Red; exit 1 }

Write-Host '[gate] 全链通过' -ForegroundColor Green
exit 0
