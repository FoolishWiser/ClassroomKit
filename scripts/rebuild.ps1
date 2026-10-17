<#
.SYNOPSIS
  还原卡/冰点机器重启后一键重建（需要管理员）。
.DESCRIPTION
  还原卡把 C 盘擦回出厂状态后，程序目录、计划任务、注册表值都会消失。
  把安装包放在不被还原的分区（如 D 盘）并加入开机计划任务或手动运行本脚本即可恢复。
#>
[CmdletBinding()]
param(
    [string]$PackageDir = "D:\ClassroomKit-Package",
    [string]$InstallDir = "C:\Program Files\ClassroomKit",
    [int]$TargetClients = 10
)

$ErrorActionPreference = "Stop"
Write-Host "=== ClassroomKit 还原后重建 ===" -ForegroundColor Cyan

if (-not (Test-Path (Join-Path $PackageDir "ClassroomKit.Agent.exe"))) {
    throw "找不到安装包：$PackageDir。请把 install.ps1 和 exe 一起放在非还原分区。"
}

& (Join-Path $PackageDir "install.ps1") -InstallDir $InstallDir -TargetClients $TargetClients

Write-Host "重建完成：热点应已恢复，托盘图标出现即成功。" -ForegroundColor Green
