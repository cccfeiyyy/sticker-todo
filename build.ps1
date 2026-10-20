<#
.SYNOPSIS
    贴纸待办（StickerTodo）一键构建发布脚本。

.DESCRIPTION
    默认发布“框架依赖单文件”（轻量，约 1~2 MB，需要本机安装 .NET 10 桌面运行时）。
    步骤：
      1. 检查 .NET SDK 版本；
      2. 检查 src\TodoSticker\Assets\app.ico，缺失/损坏时调用 tools\make-icon.py 生成；
      3. 关闭正在运行的 StickerTodo 进程（只匹配该进程名，不影响其他程序）；
      4. 清理 dist 目录；
      5. dotnet publish（Release / win-x64 / PublishSingleFile）；
      6. 校验 dist\StickerTodo.exe 存在，输出文件大小与版本信息。
    脚本兼容 Windows PowerShell 5.1 与 PowerShell 7+，可重复运行（幂等）。

.PARAMETER SelfContained
    发布自包含单文件（内置 .NET 运行时，约 150 MB+，目标机无需安装 .NET）。
    默认关闭，即框架依赖单文件。

.PARAMETER SkipIcon
    跳过图标检查/生成（app.ico 缺失时以默认图标构建）。

.PARAMETER NoClean
    构建前不清理 dist 目录（默认会先清空，避免残留旧文件）。

.PARAMETER DryRun
    只做前置检查并打印将要执行的发布命令，不关闭进程、不清理 dist、不实际发布。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1

.EXAMPLE
    pwsh -NoProfile -File .\build.ps1 -SelfContained

.EXAMPLE
    .\build.ps1 -SelfContained -NoClean

.EXAMPLE
    .\build.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$SkipIcon,
    [switch]$NoClean,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Ok([string]$Message) {
    Write-Host "  [PASS] $Message" -ForegroundColor Green
}

function Write-Warn([string]$Message) {
    Write-Host "  [WARN] $Message" -ForegroundColor Yellow
}

function Write-Fail([string]$Message) {
    Write-Host "  [FAIL] $Message" -ForegroundColor Red
}

function Get-PythonWithPillow {
    <# 返回 @{ Exe = <python 可执行文件>; Prefix = <附加参数数组>; Version = <Pillow 版本> }，
       找不到可用环境时返回 $null。 #>
    $candidates = @()
    $pythonCmd = Get-Command -Name 'python' -ErrorAction SilentlyContinue
    if ($null -ne $pythonCmd) {
        $candidates += , @{ Exe = $pythonCmd.Source; Prefix = @() }
    }
    $pyCmd = Get-Command -Name 'py' -ErrorAction SilentlyContinue
    if ($null -ne $pyCmd) {
        $candidates += , @{ Exe = $pyCmd.Source; Prefix = @('-3') }
    }
    $python3Cmd = Get-Command -Name 'python3' -ErrorAction SilentlyContinue
    if ($null -ne $python3Cmd) {
        $candidates += , @{ Exe = $python3Cmd.Source; Prefix = @() }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:DSH_BUNDLED_PYTHON) -and (Test-Path -LiteralPath $env:DSH_BUNDLED_PYTHON)) {
        $candidates += , @{ Exe = $env:DSH_BUNDLED_PYTHON; Prefix = @() }
    }

    foreach ($cand in $candidates) {
        try {
            $probeArgs = @($cand.Prefix) + @('-c', 'import PIL, sys; sys.stdout.write(PIL.__version__)')
            $version = (& $cand.Exe @probeArgs 2>$null | Out-String).Trim()
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($version)) {
                return @{ Exe = $cand.Exe; Prefix = $cand.Prefix; Version = $version }
            }
        }
        catch {
            # 某个候选不可用，继续尝试下一个
        }
    }
    return $null
}

function Get-RunningAppProcesses {
    return @(Get-Process -Name 'StickerTodo' -ErrorAction SilentlyContinue)
}

$exitCode = 0
$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($root)) {
    $root = Split-Path -Parent $MyInvocation.MyCommand.Path
}

$projectDir = Join-Path $root 'src\TodoSticker'
$projectFile = Join-Path $projectDir 'TodoSticker.csproj'
$iconFile = Join-Path $projectDir 'Assets\app.ico'
$iconScript = Join-Path $root 'tools\make-icon.py'
$distDir = Join-Path $root 'dist'
$exeFile = Join-Path $distDir 'StickerTodo.exe'

try {
    if (-not (Test-Path -LiteralPath $projectFile)) {
        throw "找不到项目文件：$projectFile（请在仓库根目录运行 build.ps1）"
    }

    Write-Host "贴纸待办 · 一键构建发布" -ForegroundColor White
    Write-Host ("PowerShell {0} / {1}" -f $PSVersionTable.PSVersion.ToString(), $PSVersionTable.PSEdition)

    # ---------------------------------------------------------------- 检查 SDK
    Write-Step '检查 .NET SDK'
    $dotnetCmd = Get-Command -Name 'dotnet' -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCmd) {
        throw '未找到 dotnet 命令。请安装 .NET 10 SDK：https://dotnet.microsoft.com/download'
    }
    $sdkVersion = (& dotnet --version | Out-String).Trim()
    $dotnetMajor = 0
    if ($sdkVersion -match '^(\d+)\.') { $dotnetMajor = [int]$Matches[1] }
    Write-Ok ("dotnet SDK {0}（{1}）" -f $sdkVersion, $dotnetCmd.Source)
    if ($dotnetMajor -lt 10) {
        Write-Warn ("项目目标框架为 net10.0-windows，当前 SDK 主版本为 {0}，构建可能失败。" -f $dotnetMajor)
    }

    # ---------------------------------------------------------------- 检查图标
    Write-Step '检查应用图标'
    $needIcon = $true
    if ($SkipIcon) {
        Write-Warn '已指定 -SkipIcon，跳过图标检查'
        $needIcon = $false
    }
    elseif (Test-Path -LiteralPath $iconFile) {
        $iconItem = Get-Item -LiteralPath $iconFile
        if ($iconItem.Length -gt 5000) {
            Write-Ok ("图标已存在：{0}（{1:N0} 字节）" -f $iconItem.FullName, $iconItem.Length)
            $needIcon = $false
        }
        else {
            Write-Warn ("图标文件异常（{0} 字节），将重新生成" -f $iconItem.Length)
        }
    }
    if ($needIcon) {
        if (-not (Test-Path -LiteralPath $iconScript)) {
            throw "缺少图标生成脚本：$iconScript"
        }
        $python = Get-PythonWithPillow
        if ($null -eq $python) {
            throw @'
未找到带 Pillow 的 Python 环境，无法生成 app.ico。
可任选其一：
  1) 安装 Pillow：python -m pip install Pillow 后重新运行 build.ps1；
  2) 手动放置一张现成图标到 src\TodoSticker\Assets\app.ico；
  3) 使用 -SkipIcon 跳过（将以默认图标构建，不推荐用于正式发布）。
'@
        }
        Write-Host ("  使用 Python（Pillow {0}）：{1}" -f $python.Version, $python.Exe)
        $iconArgs = @($python.Prefix) + @($iconScript, '--output', $iconFile, '--preview', 'none')
        & $python.Exe @iconArgs
        if ($LASTEXITCODE -ne 0) {
            throw ("图标生成失败（make-icon.py 退出码 {0}）" -f $LASTEXITCODE)
        }
        if (-not (Test-Path -LiteralPath $iconFile)) {
            throw "图标生成脚本已结束，但未找到输出文件：$iconFile"
        }
        $generated = Get-Item -LiteralPath $iconFile
        Write-Ok ("图标生成成功：{0}（{1:N0} 字节）" -f $generated.FullName, $generated.Length)
    }

    # ---------------------------------------------------------------- 发布参数
    $selfContainedValue = 'false'
    $modeName = '框架依赖单文件'
    if ($SelfContained) {
        $selfContainedValue = 'true'
        $modeName = '自包含单文件'
    }

    $publishArgs = @(
        'publish', $projectFile,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', $selfContainedValue,
        '-p:PublishSingleFile=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '--nologo',
        '-o', $distDir
    )
    $publishCommandText = 'dotnet ' + ($publishArgs -join ' ')

    if ($DryRun) {
        Write-Step 'DryRun：仅检查，不执行构建'
        Write-Host ("  发布模式：{0}（self-contained={1}）" -f $modeName, $selfContainedValue)
        Write-Host "  将要执行的步骤："
        Write-Host ("    1) 关闭正在运行的 StickerTodo 进程（当前 {0} 个）" -f (Get-RunningAppProcesses).Count)
        if ($NoClean) {
            Write-Host "    2) 保留 dist 目录（-NoClean）"
        }
        else {
            Write-Host "    2) 清理 dist 目录：$distDir"
        }
        Write-Host "    3) $publishCommandText"
        Write-Host "    4) 校验产物：$exeFile"
        Write-Host ""
        Write-Host 'DryRun 完成：未关闭进程、未清理 dist、未实际发布。' -ForegroundColor Green
        exit 0
    }

    # ---------------------------------------------------------------- 关闭进程
    Write-Step '关闭正在运行的贴纸待办进程'
    $running = Get-RunningAppProcesses
    if ($running.Count -eq 0) {
        Write-Ok '没有正在运行的 StickerTodo 进程'
    }
    else {
        Write-Host ("  发现 {0} 个 StickerTodo 进程，正在关闭 ..." -f $running.Count)
        foreach ($proc in $running) {
            try { [void]$proc.CloseMainWindow() } catch { }
        }
        Start-Sleep -Milliseconds 800
        $stillRunning = Get-RunningAppProcesses
        if ($stillRunning.Count -gt 0) {
            $stillRunning | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 500
        }
        $left = Get-RunningAppProcesses
        if ($left.Count -gt 0) {
            throw ("无法关闭 StickerTodo 进程（PID: {0}）。请手动退出后重试。" -f (($left | ForEach-Object { $_.Id }) -join ', '))
        }
        Write-Ok 'StickerTodo 进程已全部关闭'
    }

    # ---------------------------------------------------------------- 发布
    Write-Step '发布（dotnet publish）'
    if ($NoClean) {
        Write-Warn '已指定 -NoClean，保留 dist 目录中的旧文件'
    }
    elseif (Test-Path -LiteralPath $distDir) {
        Remove-Item -LiteralPath $distDir -Recurse -Force
        Write-Ok '已清理旧 dist 目录'
    }
    if (-not (Test-Path -LiteralPath $distDir)) {
        New-Item -ItemType Directory -Path $distDir -Force | Out-Null
    }

    Write-Host ("  发布模式：{0}（self-contained={1}）" -f $modeName, $selfContainedValue)
    Write-Host ("  {0}" -f $publishCommandText) -ForegroundColor DarkGray
    $publishStart = Get-Date
    & dotnet @publishArgs
    $publishExit = $LASTEXITCODE
    if ($publishExit -ne 0) {
        throw ("dotnet publish 失败（退出码 {0}），请检查上方编译错误。" -f $publishExit)
    }
    $publishSeconds = [math]::Round(((Get-Date) - $publishStart).TotalSeconds, 1)
    Write-Ok ("发布完成，用时 {0} 秒" -f $publishSeconds)

    # ---------------------------------------------------------------- 校验产物
    Write-Step '校验发布产物'
    if (-not (Test-Path -LiteralPath $exeFile)) {
        throw "发布命令成功，但未找到 dist\StickerTodo.exe。请检查 csproj 的 AssemblyName 是否为 StickerTodo。"
    }
    $exeItem = Get-Item -LiteralPath $exeFile
    $sizeMB = [math]::Round($exeItem.Length / 1MB, 2)
    $versionInfo = $exeItem.VersionInfo
    Write-Ok ("产物：{0}" -f $exeItem.FullName)
    Write-Ok ("大小：{0} MB（{1:N0} 字节）" -f $sizeMB, $exeItem.Length)
    Write-Ok ("版本：{0} / 产品：{1}" -f $versionInfo.FileVersion, $versionInfo.ProductName)

    $distFiles = @(Get-ChildItem -LiteralPath $distDir -File)
    Write-Host ("  dist 文件数：{0}" -f $distFiles.Count) -ForegroundColor DarkGray
    if ($SelfContained) {
        if ($exeItem.Length -lt 60MB) {
            Write-Warn ("自包含单文件通常应在 60 MB 以上，当前仅 {0} MB，请确认发布参数是否生效。" -f $sizeMB)
        }
        else {
            Write-Ok '自包含产物大小正常（目标机无需安装 .NET）'
        }
    }
    else {
        if ($exeItem.Length -gt 60MB) {
            Write-Warn ("框架依赖产物应较小，当前 {0} MB，可能误打成了自包含模式。" -f $sizeMB)
        }
        else {
            Write-Ok '框架依赖产物大小正常（目标机需安装 .NET 10 桌面运行时）'
        }
    }

    Write-Host ""
    Write-Host "构建成功！" -ForegroundColor Green
    Write-Host ("  运行：{0}" -f $exeFile) -ForegroundColor White
    Write-Host ("  目录：{0}" -f $distDir) -ForegroundColor White
    if (-not $SelfContained) {
        Write-Host '  提示：框架依赖版本需要 .NET 10 Desktop Runtime；如需免安装运行，请用 -SelfContained 重新发布。' -ForegroundColor DarkGray
    }
}
catch {
    Write-Host ""
    Write-Fail $_.Exception.Message
    if ($null -ne $_.InvocationInfo -and -not [string]::IsNullOrWhiteSpace($_.InvocationInfo.PositionMessage)) {
        Write-Host $_.InvocationInfo.PositionMessage -ForegroundColor DarkGray
    }
    Write-Host "构建失败，请根据上方错误信息处理后重试。" -ForegroundColor Red
    $exitCode = 1
}

exit $exitCode
