<#
.SYNOPSIS
  安装 ClassroomKit 热点守护（需要管理员）。
.DESCRIPTION
  复制程序 → 建数据目录并授权 → 写配置 → 放宽设备数上限(WifiMaxPeers) → 注册登录自启任务 → 启动。
  重复执行是安全的（幂等）。
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files\ClassroomKit",
    # 留空 = 用这台电脑的名字（防止推去别的教室时手机还粘在旧热点上）
    [string]$Ssid = "",
    # 留空 = 每台机器随机生成 8 位密码
    [string]$Passphrase = "",
    [int]$TargetClients = 10,
    [switch]$WithBCU,
    [string]$BcuSource = "",
    [switch]$SkipRegistry,
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"
$source = Split-Path -Parent $MyInvocation.MyCommand.Definition
$agent = Join-Path $InstallDir "ClassroomKit.Agent.exe"
$dataDir = Join-Path $env:ProgramData "ClassroomKit"
$configPath = Join-Path $dataDir "config.json"

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "    $text" -ForegroundColor Green }

# 热点名默认用电脑名；WLAN 的 SSID 最多 32 字节，按 UTF-8 裁，别把汉字切坏
function Get-DefaultSsid {
    $name = $env:COMPUTERNAME
    if ([string]::IsNullOrWhiteSpace($name)) { return "ClassroomKit" }
    if ([Text.Encoding]::UTF8.GetByteCount($name) -le 32) { return $name }
    $sb = New-Object Text.StringBuilder
    foreach ($ch in $name.ToCharArray()) {
        if ([Text.Encoding]::UTF8.GetByteCount($sb.ToString() + $ch) -gt 32) { break }
        [void]$sb.Append($ch)
    }
    return $sb.ToString()
}

# 密码用去掉易混字符的字母表随机 8 位（与守护里的 MachineDefaults 保持一致）
function New-RandomPassphrase {
    $alphabet = "abcdefghijkmnpqrstuvwxyz23456789"
    $chars = for ($i = 0; $i -lt 8; $i++) { $alphabet[(Get-Random -Maximum $alphabet.Length)] }
    return -join $chars
}

$current = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $current.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "请用管理员权限运行（右键 PowerShell → 以管理员身份运行）"
}

Write-Step "检查安装包"
$agentSrc = Join-Path $source "ClassroomKit.Agent.exe"
if (-not (Test-Path $agentSrc)) {
    throw "安装包不完整：找不到 $agentSrc。请把 ClassroomKit.Agent.exe 放到 install.ps1 同级目录。"
}

Write-Step "停止旧进程"
Get-Process ClassroomKit.Agent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

Write-Step "复制程序到 $InstallDir"
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $InstallDir -Recurse -Force
Write-Ok "已复制"

if ($WithBCU) {
    Write-Step "附带强力卸载工具 BCUninstaller"
    $bcuDir = Join-Path $InstallDir "BCU"
    if ($BcuSource -and (Test-Path $BcuSource)) {
        New-Item -ItemType Directory -Path $bcuDir -Force | Out-Null
        Get-ChildItem $BcuSource | Copy-Item -Destination $bcuDir -Recurse -Force
        Write-Ok "已复制 BCU 到 $bcuDir"
    } elseif (Test-Path (Join-Path $bcuDir "BCUninstaller.exe")) {
        Write-Ok "安装包里已带 BCU"
    } else {
        Write-Warning "没有找到 BCUninstaller.exe，强力卸载按钮会提示未安装（可把 BCU 便携版放到 $bcuDir）"
    }
}

Write-Step "创建数据目录并授权普通用户可写"
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $dataDir "logs") -Force | Out-Null
& icacls.exe $dataDir /grant "*S-1-5-32-545:(OI)(CI)M" /T | Out-Null
Write-Ok $dataDir

Write-Step "写入配置"
if (Test-Path $configPath) {
    Write-Ok "已有配置，保留（如需重写请先删除 $configPath）"
} else {
    if ([string]::IsNullOrWhiteSpace($Ssid)) { $Ssid = Get-DefaultSsid }
    if ([string]::IsNullOrWhiteSpace($Passphrase)) { $Passphrase = New-RandomPassphrase }
    $hex = "0123456789abcdef"
    $token = -join ((1..32) | ForEach-Object { $hex[(Get-Random -Maximum 16)] })
    $config = [ordered]@{
        schemaVersion = 2
        port = 36037
        token = $token
        hotspot = [ordered]@{
            enabled = $true
            ssid = $Ssid
            passphrase = $Passphrase
            band = "auto"
            targetClients = $TargetClients
            registryPeers = [Math]::Max($TargetClients + 6, 16)
            allowWifiUplink = $false
            guardIntervalSec = 15
            autoStartOnLaunch = $true
        }
        hosts = [ordered]@{
            enabled = $true
            refreshHours = 24
            sources = @(
                "https://raw.hellogithub.com/hosts",
                "https://cdn.jsdelivr.net/gh/521xueweihan/GitHub520@main/hosts",
                "https://ghproxy.net/https://raw.githubusercontent.com/521xueweihan/GitHub520/main/hosts"
            )
        }
        bcu = [ordered]@{ enabled = $true; path = "" }
        tray = [ordered]@{ enabled = $true }
        installedVersion = "0.1.0"
    }
    $config | ConvertTo-Json -Depth 6 | Set-Content -Path $configPath -Encoding UTF8
    Write-Ok $configPath
}

$cfg = Get-Content $configPath -Raw | ConvertFrom-Json

if (-not $SkipRegistry) {
    Write-Step "放宽热点设备数上限（WifiMaxPeers）"
    $regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\icssvc\Settings"
    if (-not (Test-Path $regPath)) { New-Item -Path $regPath -Force | Out-Null }
    $peers = [int]$cfg.hotspot.registryPeers
    New-ItemProperty -Path $regPath -Name "WifiMaxPeers" -Value $peers -PropertyType DWord -Force | Out-Null
    Write-Ok "WifiMaxPeers = $peers"
    Write-Step "重启 icssvc 让新上限生效"
    & sc.exe stop icssvc | Out-Null
    Start-Sleep -Seconds 1
    & sc.exe start icssvc | Out-Null
    Write-Ok "icssvc 已重启"
}

Write-Step "注册登录自启任务"
& $agent --install-task

Write-Step "注册 SYSTEM 护航任务（hosts 写入 + 静默升级）"
& $agent --install-service

if (-not $NoStart) {
    Write-Step "启动守护"
    # 优先用计划任务启动：它带交互令牌，能把守护送进当前登录用户的桌面会话（托盘才看得见）。
    & schtasks.exe /Run /TN "ClassroomKit-Agent" 2>$null | Out-Null
    Start-Sleep -Seconds 3
    if (-not (Get-Process -Name "ClassroomKit.Agent" -ErrorAction SilentlyContinue)) {
        Write-Warn "计划任务没把守护拉起来，改为直接启动（可能是没有已登录的桌面用户）"
        Start-Process -FilePath $agent -WindowStyle Hidden
    }
    Start-Sleep -Seconds 3
    & $agent --status
}

Write-Host ""
Write-Host "安装完成" -ForegroundColor Green
Write-Host "  控制面板：http://127.0.0.1:$($cfg.port)/?t=$($cfg.token)"
Write-Host "  热点名称：$($cfg.hotspot.ssid)    密码：$($cfg.hotspot.passphrase)"
Write-Host "  配置：$configPath"
Write-Host "  日志：$(Join-Path $dataDir 'logs')"
Write-Host ""
Write-Host "下一步：确认热点已开启 → 用手机连上试 → 再按 docs/ClassIsland.md 配课表联动" -ForegroundColor Gray
