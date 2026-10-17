<#
.SYNOPSIS
  P0 能力探针：这台一体机能不能稳定带 10 台设备。
.DESCRIPTION
  建议用管理员权限运行（读取 icssvc 注册表）。输出 JSON。
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files\ClassroomKit"
)

$ErrorActionPreference = "Continue"
$agent = Join-Path $InstallDir "ClassroomKit.Agent.exe"
if (-not (Test-Path $agent)) {
    $agent = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Definition) "ClassroomKit.Agent.exe"
}
if (-not (Test-Path $agent)) { throw "找不到 ClassroomKit.Agent.exe" }

Write-Host "==> 系统信息" -ForegroundColor Cyan
Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber | Format-List | Out-String | Write-Host

Write-Host "==> 网卡（已启用）" -ForegroundColor Cyan
Get-NetAdapter | Where-Object { $_.Status -eq "Up" } | Format-Table Name, InterfaceDescription, LinkSpeed -AutoSize | Out-String | Write-Host

Write-Host "==> 探针结果" -ForegroundColor Cyan
& $agent --probe
