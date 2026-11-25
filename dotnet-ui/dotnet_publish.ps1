# Pathwinder 管理器一键发布打包：清理 -> 自包含发布 -> Inno Setup 编译
# 任一步失败立即中止，避免把旧的/不完整的 publish 打成安装包
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# 锚定脚本所在目录，保证从任意工作目录调用都能找到 csproj / iss
Set-Location $PSScriptRoot
$iss = Join-Path $PSScriptRoot "Inno Setup.iss"

# 从 csproj 读取管理器版本号，用作安装包文件名（PathwinderManager_<版本>.exe）
$csprojPath = Join-Path $PSScriptRoot "PathwinderMgt.csproj"
$versionMatch = [regex]::Match((Get-Content $csprojPath -Raw), '<Version>([^<]+)</Version>')
if (-not $versionMatch.Success) { Write-Error "无法从 PathwinderMgt.csproj 读取 <Version>，已中止打包"; exit 1 }
$managerVersion = $versionMatch.Groups[1].Value
Write-Host "管理器版本：$managerVersion"

# 定位 Inno Setup 编译器：不写死安装路径，依次尝试 环境变量 ISCC_PATH -> PATH -> 注册表 -> 常见目录
function Resolve-IsccExe {
    if ($env:ISCC_PATH -and (Test-Path $env:ISCC_PATH)) { return $env:ISCC_PATH }

    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $uninstallRoots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    foreach ($root in $uninstallRoots) {
        if (-not (Test-Path $root)) { continue }
        foreach ($entry in Get-ChildItem $root -ErrorAction SilentlyContinue) {
            $props = Get-ItemProperty $entry.PSPath -ErrorAction SilentlyContinue
            if (-not $props) { continue }

            # 显式检查属性存在，避免开启 StrictMode 时访问缺失属性报错
            $displayName = $props.PSObject.Properties['DisplayName']
            if (-not $displayName -or $displayName.Value -notlike 'Inno Setup*') { continue }

            $installLocation = $props.PSObject.Properties['InstallLocation']
            $dir = if ($installLocation) { $installLocation.Value } else { $null }
            if (-not $dir) {
                # 部分安装方式不写 InstallLocation，退而使用 DisplayIcon（指向 ISCC.exe）
                $displayIcon = $props.PSObject.Properties['DisplayIcon']
                if ($displayIcon -and $displayIcon.Value) {
                    $dir = Split-Path ($displayIcon.Value -replace ',\d+$', '') -Parent
                }
            }
            if (-not $dir) { continue }

            $candidate = Join-Path $dir 'ISCC.exe'
            if (Test-Path $candidate) { return $candidate }
        }
    }

    $commonDirs = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6",
        "$env:ProgramFiles\Inno Setup 6",
        "${env:ProgramFiles(x86)}\Inno Setup 7",
        "$env:ProgramFiles\Inno Setup 7",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6",
        "$env:LOCALAPPDATA\Programs\Inno Setup 7",
        'D:\APP\Inno Setup 6',
        'D:\APP\Inno Setup 7'
    )
    foreach ($dir in $commonDirs) {
        $candidate = Join-Path $dir 'ISCC.exe'
        if (Test-Path $candidate) { return $candidate }
    }

    # 最后扫描各安装根目录下任意 "Inno Setup *" 文件夹（覆盖非默认盘符的安装）
    foreach ($base in @('D:\APP', $env:ProgramFiles, ${env:ProgramFiles(x86)}, "$env:LOCALAPPDATA\Programs")) {
        if (-not $base -or -not (Test-Path $base)) { continue }
        foreach ($dir in Get-ChildItem $base -Directory -Filter 'Inno Setup*' -ErrorAction SilentlyContinue) {
            $candidate = Join-Path $dir.FullName 'ISCC.exe'
            if (Test-Path $candidate) { return $candidate }
        }
    }

    return $null
}

$iscc = Resolve-IsccExe
if (-not $iscc) { Write-Error "未找到 ISCC.exe（Inno Setup 编译器）。请安装 Inno Setup，或设置环境变量 ISCC_PATH 指向 ISCC.exe"; exit 1 }
Write-Host "ISCC：$iscc"

# 定位 dotnet：不依赖当前终端的 PATH（SDK 安装后旧终端不会自动刷新）
function Resolve-DotnetExe {
    if ($env:DOTNET_ROOT) {
        $candidate = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
        if (Test-Path $candidate) { return $candidate }
    }

    $onPath = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $candidates = @(
        "$env:ProgramFiles\dotnet\dotnet.exe",
        "${env:ProgramFiles(x86)}\dotnet\dotnet.exe",
        "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe",
        'D:\APP\dotnet\dotnet.exe',
        'C:\Program Files\dotnet\dotnet.exe'
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }

    return $null
}

$dotnet = Resolve-DotnetExe
if (-not $dotnet) { Write-Error "未找到 dotnet.exe（.NET SDK）。请安装 .NET SDK 8，或设置环境变量 DOTNET_ROOT 指向 SDK 安装目录"; exit 1 }
Write-Host "dotnet：$dotnet"

Remove-Item -Recurse -Force .\publish -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force .\bin -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force .\obj -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force .\output -ErrorAction SilentlyContinue

& $dotnet publish ".\PathwinderMgt.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish 失败，退出码 $LASTEXITCODE，已中止打包"; exit $LASTEXITCODE }

& $iscc "/DManagerVersion=$managerVersion" $iss
if ($LASTEXITCODE -ne 0) { Write-Error "ISCC 编译安装包失败，退出码 $LASTEXITCODE"; exit $LASTEXITCODE }

Write-Host "打包完成：Output\PathwinderManager_$managerVersion.exe" -ForegroundColor Green
