<#
.SYNOPSIS
  给「强力卸载」按钮装工具：下载 BCUninstaller 便携版并解到程序的 BCU 子目录。
.DESCRIPTION
  面板/托盘里的「强力卸载」只是拉起 BCUninstaller（Apache-2.0），不自己写卸载逻辑。
  BCU 便携版自带 .NET 运行时（约 100 MB），不需要目标机装 .NET，解压即用。

  这个脚本在目标机上跑一次就行：
    powershell -ExecutionPolicy Bypass -File get-bcu.ps1
  需要管理员权限（要写 C:\Program Files）。下载慢时可以加 -Mirror 换镜像。
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files\ClassroomKit",
    [string]$Version = "6.3.0",
    [string]$Mirror = "",
    [string]$SevenZip = "",
    [switch]$KeepArchive
)

$ErrorActionPreference = "Stop"

function Write-Step($t) { Write-Host "==> $t" -ForegroundColor Cyan }
function Write-Ok($t) { Write-Host "    $t" -ForegroundColor Green }
function Write-Warn($t) { Write-Host "    $t" -ForegroundColor Yellow }

$current = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $current.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "请用管理员权限运行（右键 PowerShell → 以管理员身份运行）"
}

$bcuDir = Join-Path $InstallDir "BCU"
$target = Join-Path $bcuDir "BCUninstaller.exe"
if (Test-Path $target) {
    Write-Ok "已经有 $target，不重复下载（要重装请先删掉这个目录）"
    exit 0
}

$work = Join-Path $env:TEMP ("ck-bcu-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $work -Force | Out-Null

$archive = Join-Path $work "bcu.7z"
$url = "https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/releases/download/v$Version/BCUninstaller_${Version}_portable.7z"
$mirrors = @(
    "",
    "https://gh-proxy.com/",
    "https://ghproxy.net/"
)

Write-Step "下载 BCUninstaller $Version 便携版（约 100 MB，慢的话耐心等）"
$ok = $false
foreach ($m in $mirrors) {
    $use = $m + $url
    if ($Mirror -and $m -ne "") { continue }
    if ($Mirror) { $use = $Mirror.TrimEnd("/") + "/" + $url }
    try {
        Write-Host "    来源：$use" -ForegroundColor DarkGray
        & curl.exe -L --retry 2 --max-time 900 -o $archive $use
        if ($LASTEXITCODE -eq 0 -and (Test-Path $archive) -and ((Get-Item $archive).Length -gt 50MB)) { $ok = $true; break }
        Write-Warn "这次没下完整，换下一个来源"
    } catch {
        Write-Warn "失败：$($_.Exception.Message)"
    }
    if ($Mirror) { break }
}
if (-not $ok) {
    throw "下载失败。可以手动下 $url，解压后把 BCUninstaller.exe 放到 $bcuDir"
}
Write-Ok ("已下载 " + [Math]::Round((Get-Item $archive).Length / 1MB) + " MB")

Write-Step "准备解压工具"
$exe = $SevenZip
if (-not $exe -or -not (Test-Path $exe)) {
    $candidates = @(
        "C:\Program Files\7-Zip\7z.exe",
        "C:\Program Files (x86)\7-Zip\7z.exe",
        (Join-Path $env:TEMP "7zr.exe")
    )
    $exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $exe) {
    $exe = Join-Path $work "7zr.exe"
    Write-Host "    下载 7zr.exe（7-Zip 官方单文件版，约 600 KB）" -ForegroundColor DarkGray
    & curl.exe -L --retry 2 --max-time 120 -o $exe "https://www.7-zip.org/a/7zr.exe"
    if (-not (Test-Path $exe) -or (Get-Item $exe).Length -lt 100KB) {
        throw "拿不到 7zr.exe。请先装 7-Zip，或手动解压便携版到 $bcuDir"
    }
}
Write-Ok $exe

Write-Step "解压到 $bcuDir"
New-Item -ItemType Directory -Path $bcuDir -Force | Out-Null
& $exe x $archive "-o$bcuDir" -y | Out-Null
if ($LASTEXITCODE -ne 0) { throw "解压失败（压缩包可能没下全）。删掉 $bcuDir 重新跑一次" }
if (-not (Test-Path $target)) { throw "解压完了但没找到 BCUninstaller.exe，请检查 $bcuDir" }
Write-Ok $target

if (-not $KeepArchive) { Remove-Item $archive -Force -ErrorAction SilentlyContinue }
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "完成：面板/托盘的「强力卸载」现在能直接拉起 BCUninstaller 了" -ForegroundColor Green
Write-Host "  （热点守护不用重启，按钮实时查找这个路径）" -ForegroundColor Gray
