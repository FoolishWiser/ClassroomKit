<#
.SYNOPSIS
  卸载 ClassroomKit（需要管理员）。
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files\ClassroomKit",
    [switch]$RemoveData,
    [switch]$RestorePeerLimit
)

$ErrorActionPreference = "Continue"
$dataDir = Join-Path $env:ProgramData "ClassroomKit"
$agent = Join-Path $InstallDir "ClassroomKit.Agent.exe"

Write-Host "==> 关闭热点" -ForegroundColor Cyan
if (Test-Path $agent) { & $agent --off | Out-Null }

Write-Host "==> 停止守护进程" -ForegroundColor Cyan
Get-Process ClassroomKit.Agent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

Write-Host "==> 删除自启任务" -ForegroundColor Cyan
& schtasks.exe /Delete /TN "ClassroomKit-Agent" /F 2>&1 | Out-Null

if ($RestorePeerLimit) {
    Write-Host "==> 恢复默认设备数上限" -ForegroundColor Cyan
    & reg.exe delete "HKLM\SYSTEM\CurrentControlSet\Services\icssvc\Settings" /v WifiMaxPeers /f 2>&1 | Out-Null
    & sc.exe stop icssvc | Out-Null
    Start-Sleep -Seconds 1
    & sc.exe start icssvc | Out-Null
}

Write-Host "==> 删除程序目录" -ForegroundColor Cyan
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }

if ($RemoveData) {
    Write-Host "==> 删除配置与日志" -ForegroundColor Cyan
    if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force }
}

Write-Host "卸载完成（热点本身是系统功能，不受影响）" -ForegroundColor Green
