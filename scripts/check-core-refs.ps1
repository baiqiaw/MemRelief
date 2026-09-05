# 法-1 引用门禁：检查 Core 编译产物的程序集引用清单，禁止引入 WPF/WinForms UI 程序集（含 NuGet 传递引入）。
# 用法: pwsh scripts/check-core-refs.ps1 [-CoreDll <路径>]
#   默认检 Debug 产物；验 Release 或其他配置须显式传 -CoreDll。
# 退出码: 0=通过，1=命中禁止引用，2=产物不存在/身份不符（须为 MemRelief.Core）/读取失败
param(
    [string]$CoreDll = "$PSScriptRoot\..\src\MemRelief.Core\bin\Debug\net10.0\MemRelief.Core.dll"
)

$ErrorActionPreference = 'Stop'
# 门禁结论常经管道/重定向采集（issue 证据），固定 UTF-8 输出
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if (-not (Test-Path -LiteralPath $CoreDll)) {
    Write-Host "[引用门禁] FAIL：产物不存在 $CoreDll（先 dotnet build）" -ForegroundColor Red
    exit 2
}

# 禁止清单：法-1 字面（WindowsBase/PresentationFramework/System.Windows.*）+ WPF 同族加固。
# 注意：PresentationCore/PresentationFramework.Aero 是完整单词（'Presentation' 后无点），
# 必须显式枚举，'Presentation.' 前缀匹配不到它们。
# 大小写不敏感匹配：程序集名大小写在元数据中任意、运行时绑定不区分，防小写变体漏判。
$BannedExact = @(
    'WindowsBase', 'PresentationFramework', 'PresentationCore', 'System.Xaml',
    'PresentationFramework.Aero', 'PresentationFramework.Aero2', 'PresentationFramework.Classic',
    'PresentationFramework.Luna', 'PresentationFramework.Royale', 'PresentationFramework.SystemColors',
    'ReachFramework', 'System.Printing', 'System.Windows.Presentation'
)
$BannedPrefixes = @('System.Windows.', 'UIAutomation.')

Add-Type -AssemblyName System.Reflection.Metadata | Out-Null

try {
    $stream = [System.IO.File]::OpenRead($CoreDll)
    try {
        $peReader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            if (-not $peReader.HasMetadata) {
                Write-Host "[引用门禁] FAIL：非托管程序集 $CoreDll" -ForegroundColor Red
                exit 2
            }
            $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
            # 身份锚点：防止把任意干净 dll 传进来"指鹿为马"骗过门禁
            $asmName = $md.GetString($md.GetAssemblyDefinition().Name)
            if ($asmName -ne 'MemRelief.Core') {
                Write-Host "[引用门禁] FAIL：产物身份不符（$asmName ≠ MemRelief.Core）$CoreDll" -ForegroundColor Red
                exit 2
            }
            $refs = @()
            foreach ($handle in $md.AssemblyReferences) {
                $ar = $md.GetAssemblyReference($handle)
                $refs += $md.GetString($ar.Name)
            }
            $refs = @($refs | Sort-Object -Unique)
        } finally {
            $peReader.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
} catch {
    Write-Host "[引用门禁] FAIL：产物读取失败 $CoreDll → $($_.Exception.GetType().Name): $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

$hits = @($refs | Where-Object {
    $name = $_
    ($BannedExact | Where-Object { $_ -ieq $name }) -or ($BannedPrefixes | Where-Object { $name.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })
})

Write-Host "[引用门禁] 产物: $CoreDll（$((Get-Item -LiteralPath $CoreDll).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))）"
Write-Host "[引用门禁] 程序集引用 ($($refs.Count) 项): $($refs -join ', ')"

if ($hits.Count -gt 0) {
    Write-Host "[引用门禁] FAIL：Core 引用了 UI 程序集 → $($hits -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Host "[引用门禁] PASS：无 UI 程序集引用" -ForegroundColor Green
exit 0
