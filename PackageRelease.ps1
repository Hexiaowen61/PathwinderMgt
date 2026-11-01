#Requires -Version 5.1
<#
.SYNOPSIS
    One-shot release pipeline:
      [1/4] Build Pathwinder HookModule (Release x64 + Win32)
      [2/4] Build Release|Win32 (labeled separately for clarity)
      [3/4] Package Pathwinder-v<Version>-ui<date>.zip into Output\Package, then replace
            dotnet-ui\resources\Pathwinder-*.zip with it (upstream zip layout)
      [4/4] Build the UI installer (dotnet publish + Inno Setup) and print the final exe path
.NOTES
    The Pathwinder engine version is defined below via $Version.
    The manager UI version is read from dotnet-ui\PathwinderMgt.csproj by dotnet_publish.ps1.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Anchored to this script's own directory (the repository root), so the project works no matter
# where it is cloned. Engines and UI both resolve their own paths relative to this.
$ProjectRoot    = $PSScriptRoot
$Solution       = Join-Path $ProjectRoot 'Pathwinder.sln'
$PackageDir     = Join-Path $ProjectRoot 'Output\Package'
$UiResources    = Join-Path $ProjectRoot 'dotnet-ui\resources'
$VsWhere        = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"

# Pathwinder engine release version (defined here on purpose, not derived from git).
$Version        = '1.2.0'

if (-not (Test-Path $VsWhere)) {
    throw "vswhere.exe not found: $VsWhere`nInstall Visual Studio 2022 or BuildTools first."
}

Write-Host 'Locating MSBuild...' -ForegroundColor Cyan
$msbuild = $null

# 1) vswhere without component filter (BuildTools may not report MSBuild component ID)
try {
    $msbuild = & $VsWhere -latest -products * `
        -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null | Select-Object -First 1
} catch {}

# 2) Fallback: search Visual Studio install roots
if (-not $msbuild -or -not (Test-Path $msbuild)) {
    $vsRoots = @(
        'C:\Program Files\Microsoft Visual Studio',
        'C:\Program Files (x86)\Microsoft Visual Studio'
    )
    $found = foreach ($root in $vsRoots) {
        if (Test-Path $root) {
            Get-ChildItem $root -Recurse -Filter MSBuild.exe -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\Bin\\MSBuild\.exe$' } |
                Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
        }
    }
    $msbuild = $found | Select-Object -First 1
}

# 3) Last resort: .NET Framework MSBuild
if (-not $msbuild -or -not (Test-Path $msbuild)) {
    $fxMsbuild = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
    if (Test-Path $fxMsbuild) { $msbuild = $fxMsbuild }
}

if (-not $msbuild -or -not (Test-Path $msbuild)) {
    throw 'MSBuild.exe not found. Install VS2022 BuildTools with the C++ workload.'
}
Write-Host "MSBuild: $msbuild" -ForegroundColor Green

$env:CL = '/utf-8 /wd4828 /wd4819'
Push-Location $ProjectRoot

try {
    Write-Host "`n[1/4] Building Release|x64 ..." -ForegroundColor Cyan
    & $msbuild $Solution /t:Pathwinder /p:Configuration=Release /p:Platform=x64 /m /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "x64 build failed, exit code $LASTEXITCODE" }

    Write-Host "`n[2/4] Building Release|Win32 ..." -ForegroundColor Cyan
    & $msbuild $Solution /t:Pathwinder /p:Configuration=Release /p:Platform=Win32 /m /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "Win32 build failed, exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

$out64 = Join-Path $ProjectRoot 'Output\Release\x64\Pathwinder.HookModule.64.dll'
$out32 = Join-Path $ProjectRoot 'Output\Release\Win32\Pathwinder.HookModule.32.dll'

if (-not (Test-Path $out64)) { throw "x64 output missing: $out64" }
if (-not (Test-Path $out32)) { throw "Win32 output missing: $out32" }

$size64 = (Get-Item $out64).Length
$size32 = (Get-Item $out32).Length

Write-Host "`nOutput verification:" -ForegroundColor Cyan
Write-Host "  x64: $size64 bytes"
Write-Host "  x86: $size32 bytes"

# --------------------------------------------------------------------------------------------------
# [3/4] Package a release zip in the upstream GitHub-release layout, then replace the copy in the
#       UI resources folder. The manager app discovers the zip via the "Pathwinder-*.zip" wildcard,
#       so the old zip must be removed; the replacement happens BEFORE the UI installer is built,
#       which makes the new zip part of the final Setup exe. The "-ui<date>" suffix distinguishes
#       this fork's builds from upstream release archives.
# --------------------------------------------------------------------------------------------------

$dateStamp = Get-Date -Format 'yyyyMMdd'
$zipName = "Pathwinder-v$Version-ui$dateStamp"
$zipPath = Join-Path $PackageDir "$zipName.zip"
$staging = Join-Path $PackageDir $zipName

Write-Host "`n[3/4] Packaging $zipName ..." -ForegroundColor Cyan

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

$layout = @(
    @{ Src = Join-Path $ProjectRoot 'LICENSE';                     Dst = 'LICENSE' },
    @{ Src = Join-Path $ProjectRoot 'README.md';                   Dst = 'README.md' },
    @{ Src = Join-Path $ProjectRoot 'ThirdParty\Hookshot\LICENSE'; Dst = 'ThirdParty\Hookshot_LICENSE' },
    @{ Src = Join-Path $ProjectRoot 'ThirdParty\XstdBitSet\LICENSE'; Dst = 'ThirdParty\XstdBitSet_LICENSE' },
    @{ Src = $out64;                                               Dst = 'x64\Pathwinder.HookModule.64.dll' },
    @{ Src = $out32;                                               Dst = 'Win32\Pathwinder.HookModule.32.dll' }
)

foreach ($item in $layout) {
    $destination = Join-Path $staging $item.Dst
    $destinationDir = Split-Path $destination -Parent
    if (-not (Test-Path $destinationDir)) { New-Item $destinationDir -ItemType Directory -Force | Out-Null }
    Copy-Item $item.Src $destination -Force
}

Compress-Archive -Path $staging -DestinationPath $zipPath -CompressionLevel Optimal
Remove-Item $staging -Recurse -Force

$zip = Get-Item $zipPath
Write-Host "  Package: $zipPath" -ForegroundColor Green
Write-Host "  Size: $($zip.Length) bytes"

Write-Host "  Replacing UI resources (removing old Pathwinder-*.zip) ..." -ForegroundColor Cyan
Get-ChildItem $UiResources -Filter 'Pathwinder-*.zip' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force
Copy-Item $zipPath $UiResources -Force
Write-Host "  Resources: $zipPath -> $UiResources" -ForegroundColor Green

Write-Host "`n[4/4] Building UI installer (dotnet publish + Inno Setup) ..." -ForegroundColor Cyan
& (Join-Path $ProjectRoot 'dotnet-ui\dotnet_publish.ps1')
if ($LASTEXITCODE -ne 0) { throw "UI packaging failed, exit code $LASTEXITCODE" }

$setupExe = Get-ChildItem (Join-Path $ProjectRoot 'dotnet-ui\Output\PathwinderManager_*.exe') -File |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $setupExe) { throw 'Setup exe not found in dotnet-ui\Output.' }

Write-Host "`nFinal installer:" -ForegroundColor Green
Write-Host "  $($setupExe.FullName)"

Write-Host "`nDone." -ForegroundColor Green