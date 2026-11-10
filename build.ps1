# SPDX-License-Identifier: GPL-3.0-only
# Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
# 바꿈 만들기. 윈도우에 들어 있는 C# 5 컴파일러(csc.exe)만 씁니다(따로 설치할 것 없음).
# 사용(PowerShell):
#   (이 파일이 있는 폴더로 cd 한 뒤)
#   powershell -ExecutionPolicy Bypass -File .\build.ps1          → build\Bakkum.exe
#   powershell -ExecutionPolicy Bypass -File .\build.ps1 -Zip     → dist\바꿈-0.1.0.zip 까지
param([switch]$Zip)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$wpf = Join-Path $fw 'WPF'
$gac = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL'
$meta = Join-Path $env:WINDIR 'System32\WinMetadata'
$out = Join-Path $root 'build'
New-Item -ItemType Directory -Force $out | Out-Null

$refs = @(
    "$wpf\PresentationCore.dll", "$wpf\PresentationFramework.dll", "$wpf\WindowsBase.dll",
    "$fw\System.Xaml.dll", "$fw\System.dll", "$fw\System.Core.dll", "$fw\System.Xml.dll",
    "$fw\Microsoft.CSharp.dll", "$fw\System.Drawing.dll", "$fw\System.Windows.Forms.dll",
    "$fw\System.Security.dll", "$fw\System.Web.Extensions.dll", "$fw\System.Runtime.WindowsRuntime.dll",
    "$fw\System.IO.Compression.dll",
    "$gac\System.Runtime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.dll",
    "$gac\System.Runtime.InteropServices.WindowsRuntime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.InteropServices.WindowsRuntime.dll",
    "$gac\System.Threading.Tasks\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Threading.Tasks.dll",
    "$gac\System.IO\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.IO.dll"
)
foreach ($w in 'Foundation', 'Globalization', 'Graphics', 'Media', 'Storage', 'Data', 'UI', 'System') {
    $refs += Join-Path $meta "Windows.$w.winmd"
}
foreach ($r in $refs) { if (-not (Test-Path $r)) { throw "참조 파일이 없습니다: $r" } }

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs -Recurse | ForEach-Object { $_.FullName }
$icon = Join-Path $root 'assets\app.ico'
$exe = Join-Path $out 'Bakkum.exe'
$notice = Join-Path $root 'NOTICE.txt'

$cscArgs = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', '/utf8output', '/codepage:65001', '/warn:4', '/nowarn:1591',
    "/out:$exe", "/win32icon:$icon", "/resource:$icon,Bakkum.app.ico")
if (Test-Path $notice) { $cscArgs += "/resource:$notice,Bakkum.NOTICE.txt" }
foreach ($r in $refs) { $cscArgs += "/reference:$r" }
$cscArgs += $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "컴파일 실패 (코드 $LASTEXITCODE)" }
Write-Host "만들었습니다: $exe ($([math]::Round((Get-Item $exe).Length / 1KB)) KB)"

if ($Zip) {
    $ver = (Get-Item $exe).VersionInfo.ProductVersion
    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Force $dist | Out-Null
    $stage = Join-Path $dist 'stage'
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force $stage | Out-Null
    Copy-Item $exe (Join-Path $stage '바꿈 설치.exe')
    if (Test-Path $notice) { Copy-Item $notice (Join-Path $stage 'NOTICE.txt') }
    $readme = Join-Path $root '설치 안내.txt'
    if (Test-Path $readme) { Copy-Item $readme (Join-Path $stage '설치 안내.txt') }
    $zipPath = Join-Path $dist ("바꿈-" + $ver + ".zip")
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false, [System.Text.Encoding]::UTF8)
    Remove-Item -Recurse -Force $stage
    Write-Host "묶었습니다: $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1KB)) KB)"
    # 앱 안 업데이트에 올릴 것: GitHub Releases에 Bakkum.exe, 사이트(bakkum 폴더)에 version.json
    Copy-Item $exe (Join-Path $dist 'Bakkum.exe') -Force
    $sha = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = @{ version = $ver; url = "https://github.com/hirame-ki/bakkum/releases/download/v$ver/Bakkum.exe"; sha256 = $sha; notes = "" } | ConvertTo-Json
    [System.IO.File]::WriteAllText((Join-Path $dist 'version.json'), $manifest, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "업데이트용: dist\Bakkum.exe, dist\version.json (notes 칸에 고친 점을 적어 올리세요)"
}
