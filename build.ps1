<#
.SYNOPSIS
    编译并打包 Just_Lilith（BepInEx IL2CPP 插件）。

.DESCRIPTION
    两个程序集都使用 LangVersion 14.0，需要 .NET SDK 10 或更高版本编译。
    Just_Lilith.Unity 通过 $(LilithBepInExDir) 引用游戏目录下的
    BepInEx\core 与 BepInEx\interop 程序集（interop 由 BepInEx 在首次启动游戏时生成）。

.PARAMETER Configuration
    Debug 或 Release，默认 Release。

.PARAMETER BepInExDir
    游戏目录下的 BepInEx 文件夹路径。优先级：
    -BepInExDir > 环境变量 LILITH_BEPINEX_DIR > 内置候选路径。
    该文件夹需要同时包含 core\BepInEx.Core.dll 与 interop\Assembly-CSharp.dll。

.PARAMETER GameDir
    游戏根目录，用于 -Install 时定位插件目录。优先级：
    -GameDir > 环境变量 LILITH_GAME_DIR > 与 BepInExDir 同级推断。

.PARAMETER Dotnet
    指定 dotnet 可执行文件路径。默认依次尝试 D:\dotnet10\dotnet.exe、
    $env:DOTNET_ROOT\dotnet.exe、PATH 中的 dotnet。

.PARAMETER Package
    额外生成 dist\Just_Lilith-<版本>.zip，内部结构为
    Just_Lilith\BepInEx\plugins\Just_Lilith\{Just_Lilith.Core,Just_Lilith.Unity}.dll，
    与官方发布包一致。

.PARAMETER Install
    额外把两个 DLL 复制到游戏插件目录（先备份被覆盖的文件）。

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -Package

.EXAMPLE
    .\build.ps1 -BepInExDir 'E:\YYGameBox\Steam\steamapps\common\The NOexistenceN of Lilith\BepInEx' -Install
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$BepInExDir = $env:LILITH_BEPINEX_DIR,
    [string]$GameDir = $env:LILITH_GAME_DIR,
    [string]$Dotnet,
    [switch]$Package,
    [switch]$Install
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$coreProject = Join-Path $root 'Just_Lilith.Core\Just_Lilith.Core.csproj'
$unityProject = Join-Path $root 'Just_Lilith.Unity\Just_Lilith.Unity.csproj'

function Write-Step([string]$Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Resolve-Dotnet {
    param([string]$Explicit)

    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($Explicit) { $candidates.Add($Explicit) }
    $candidates.Add('D:\dotnet10\dotnet.exe')
    if ($env:DOTNET_ROOT) { $candidates.Add((Join-Path $env:DOTNET_ROOT 'dotnet.exe')) }
    $candidates.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'))
    $candidates.Add('C:\Program Files\dotnet\dotnet.exe')

    $pathDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($pathDotnet) { $candidates.Add($pathDotnet.Source) }

    foreach ($candidate in $candidates) {
        if (-not (Test-Path $candidate)) { continue }
        $sdks = & $candidate --list-sdks 2>$null
        if (-not $sdks) { continue }
        # LangVersion 14.0 需要 SDK 10+
        $hasModern = @($sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 10 }).Count -gt 0
        if ($hasModern) {
            Write-Host "dotnet: $candidate"
            return $candidate
        }
        Write-Host "跳过 $candidate（缺少 .NET SDK 10+，无法编译 LangVersion 14.0）" -ForegroundColor DarkYellow
    }

    throw @'
未找到 .NET SDK 10+。
可执行（无需管理员，装到 D:\dotnet10）：
  Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
  .\dotnet-install.ps1 -Channel 10.0 -InstallDir D:\dotnet10 -NoPath
然后用 -Dotnet D:\dotnet10\dotnet.exe 或设置 DOTNET_ROOT 重新运行。
'@
}

function Test-BepInExDir([string]$Path) {
    if (-not $Path) { return $false }
    return (Test-Path (Join-Path $Path 'core\BepInEx.Core.dll')) -and
           (Test-Path (Join-Path $Path 'interop\Assembly-CSharp.dll'))
}

function Resolve-BepInExDir {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-BepInExDir $Explicit)) {
            throw "BepInExDir 不可用：$Explicit（需要 core\BepInEx.Core.dll 和 interop\Assembly-CSharp.dll）"
        }
        return (Resolve-Path $Explicit).Path
    }

    $candidates = @(
        'E:\YYGameBox\Steam\steamapps\common\The NOexistenceN of Lilith\BepInEx',
        'D:\YYGameBox\Steam\steamapps\common\The NOexistenceN of Lilith\BepInEx',
        'C:\Program Files (x86)\Steam\steamapps\common\The NOexistenceN of Lilith\BepInEx',
        'D:\SteamLibrary\steamapps\common\The NOexistenceN of Lilith\BepInEx'
    )
    foreach ($candidate in $candidates) {
        if (Test-BepInExDir $candidate) {
            Write-Host "BepInEx: $candidate"
            return $candidate
        }
    }

    throw @'
未找到可用的 BepInEx 目录（需要包含 interop 的已安装游戏目录）。
请用 -BepInExDir 指定，或设置环境变量 LILITH_BEPINEX_DIR：
  $env:LILITH_BEPINEX_DIR = 'E:\YYGameBox\Steam\steamapps\common\The NOexistenceN of Lilith\BepInEx'
interop 程序集由 BepInEx 在游戏成功启动一次后生成。
'@
}

function Get-PluginVersion {
    $pluginFile = Join-Path $root 'Just_Lilith.Unity\Just_Lilith\Unity\Plugin.cs'
    $match = Select-String -Path $pluginFile -Pattern 'public const string Version = "([^"]+)"' |
        Select-Object -First 1
    if ($match) { return $match.Matches[0].Groups[1].Value }
    return '0.0.0'
}

# --- 环境准备 -------------------------------------------------------------
$dotnetExe = Resolve-Dotnet -Explicit $Dotnet
$bepInEx = Resolve-BepInExDir -Explicit $BepInExDir

# --- 编译 ----------------------------------------------------------------
Write-Step "编译 Just_Lilith.Core（$Configuration）"
& $dotnetExe build $coreProject -c $Configuration --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Just_Lilith.Core 编译失败（exit $LASTEXITCODE）" }

Write-Step "编译 Just_Lilith.Unity（$Configuration）"
& $dotnetExe build $unityProject -c $Configuration --nologo -v minimal "-p:LilithBepInExDir=$bepInEx"
if ($LASTEXITCODE -ne 0) { throw "Just_Lilith.Unity 编译失败（exit $LASTEXITCODE）" }

$framework = if ($Configuration -eq 'Debug') { 'net6.0' } else { 'net6.0' }
$outputDir = Join-Path $root "Just_Lilith.Unity\bin\$Configuration\$framework"
$artifacts = @('Just_Lilith.Core.dll', 'Just_Lilith.Unity.dll')
foreach ($name in $artifacts) {
    $path = Join-Path $outputDir $name
    if (-not (Test-Path $path)) { throw "缺少编译产物：$path" }
    $length = (Get-Item $path).Length
    Write-Host ("产物 {0,-24} {1,10:N0} 字节" -f $name, $length)
}

# --- 打包 ----------------------------------------------------------------
$version = Get-PluginVersion
if ($Package) {
    Write-Step "打包 Just_Lilith $version"
    $distDir = Join-Path $root 'dist'
    $stageDir = Join-Path $root 'obj\pkg\Just_Lilith\BepInEx\plugins\Just_Lilith'
    if (Test-Path (Join-Path $root 'obj\pkg')) { Remove-Item (Join-Path $root 'obj\pkg') -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null

    foreach ($name in $artifacts) {
        Copy-Item (Join-Path $outputDir $name) (Join-Path $stageDir $name) -Force
    }

    $zipPath = Join-Path $distDir "Just_Lilith-$version.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $root 'obj\pkg\Just_Lilith') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "发布包：$zipPath（$((Get-Item $zipPath).Length) 字节）" -ForegroundColor Green
}

# --- 部署（可选） ---------------------------------------------------------
if ($Install) {
    if (-not $GameDir) {
        $parent = Split-Path -Parent $bepInEx
        if (Test-Path (Join-Path $parent 'Lilith.exe')) { $GameDir = $parent }
    }
    if (-not $GameDir) { throw '-Install 需要 -GameDir 或环境变量 LILITH_GAME_DIR' }

    $pluginDir = Join-Path $GameDir 'BepInEx\plugins\Just_Lilith'
    if (-not (Test-Path $pluginDir)) { throw "插件目录不存在：$pluginDir" }
    if (Get-Process -Name Lilith -ErrorAction SilentlyContinue) { throw '游戏正在运行，请先退出游戏再 -Install' }

    # 备份必须放在 plugins 扫描范围之外：BepInEx 6 会递归扫描 plugins，
    # 备份里的同名程序集会被当成重复插件（日志里的 “Skipping ... because a newer version exists”）。
    $bepInExRoot = Split-Path -Parent (Split-Path -Parent $pluginDir)
    $backupDir = Join-Path $bepInExRoot ('plugins-backup\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    Write-Step "部署到 $pluginDir（旧文件备份至 $backupDir）"
    foreach ($name in $artifacts) {
        $target = Join-Path $pluginDir $name
        if (Test-Path $target) { Copy-Item $target (Join-Path $backupDir $name) -Force }
        Copy-Item (Join-Path $outputDir $name) $target -Force
        Write-Host "已部署 $name"
    }
}

Write-Step "完成"
Write-Host "输出目录：$outputDir"
