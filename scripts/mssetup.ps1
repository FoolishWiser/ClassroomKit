<#
.SYNOPSIS
  MSI 安装包内部使用的机器级配置脚本（由 Windows Installer 以 SYSTEM 身份调用）。
.DESCRIPTION
  与 install.ps1 的区别：这里不复制文件（文件由 MSI 负责），只做 MSI 做不到/不方便做的事：
    建数据目录并授权、生成配置（已有则保留）、放宽 WifiMaxPeers、注册登录自启任务、启动守护。
  卸载时反向清理。手工执行也可以（等价于 install.ps1 / uninstall.ps1 的核心步骤）。

  用法（一般由 MSI 自动调用）：
    powershell -File mssetup.ps1 -Mode install
    powershell -File mssetup.ps1 -Mode stop
    powershell -File mssetup.ps1 -Mode uninstall [-KeepData]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("install", "stop", "uninstall")]
    [string]$Mode,
    [string]$InstallDir = "C:\Program Files\ClassroomKit",
    [string]$Ssid = "CLASSROOM-302",
    [string]$Passphrase = "80258025",
    [int]$TargetClients = 10,
    [switch]$KeepData,
    [switch]$SkipRegistry,
    [string]$LogPath = ""
)

$ErrorActionPreference = "Continue"
$agent = Join-Path $InstallDir "ClassroomKit.Agent.exe"
$dataDir = Join-Path $env:ProgramData "ClassroomKit"
$configPath = Join-Path $dataDir "config.json"
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\icssvc\Settings"
if (-not $LogPath) { $LogPath = Join-Path $env:TEMP ("ck-mssetup-" + $Mode + ".log") }

function Log([string]$text) {
    $line = "{0} [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Mode, $text
    Write-Host $line
    try { Add-Content -Path $LogPath -Value $line -Encoding UTF8 -ErrorAction Stop } catch { }
}

function Stop-Agent {
    $procs = Get-Process ClassroomKit.Agent -ErrorAction SilentlyContinue
    if ($procs) {
        Log ("停止守护进程 PID=" + (($procs | ForEach-Object { $_.Id }) -join ","))
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    } else {
        Log "没有正在运行的守护进程"
    }
}

function Restart-Icssvc {
    & sc.exe stop icssvc | Out-Null
    Start-Sleep -Milliseconds 1200
    & sc.exe start icssvc | Out-Null
    Start-Sleep -Milliseconds 600
}

Log "开始（安装目录 $InstallDir）"

switch ($Mode) {
    "stop" {
        # MSI 删文件前先停进程，否则文件占用会推迟到重启才删除
        Stop-Agent
    }

    "install" {
        if (-not (Test-Path $agent)) { Log "错误：找不到 $agent"; exit 1603 }

        Stop-Agent

        Log "创建数据目录并授权普通用户可写：$dataDir"
        New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $dataDir "logs") -Force | Out-Null
        & icacls.exe $dataDir /grant "*S-1-5-32-545:(OI)(CI)M" /T | Out-Null

        if (Test-Path $configPath) {
            Log "已有配置，保留"
        } else {
            $hex = "0123456789abcdef"
            $token = -join ((1..32) | ForEach-Object { $hex[(Get-Random -Maximum 16)] })
            $config = [ordered]@{
                port           = 36037
                token          = $token
                hotspot        = [ordered]@{
                    enabled           = $true
                    ssid              = $Ssid
                    passphrase        = $Passphrase
                    band              = "auto"
                    targetClients     = $TargetClients
                    registryPeers     = [Math]::Max($TargetClients + 6, 16)
                    allowWifiUplink   = $false
                    guardIntervalSec  = 15
                    autoStartOnLaunch = $true
                }
                bcu            = [ordered]@{ enabled = $true; path = "" }
                tray           = [ordered]@{ enabled = $true }
                installedVersion = "0.1.0"
            }
            $config | ConvertTo-Json -Depth 6 | Set-Content -Path $configPath -Encoding UTF8
            Log "写入配置 $configPath"
        }

        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json

        if (-not $SkipRegistry) {
            if (-not (Test-Path $regPath)) { New-Item -Path $regPath -Force | Out-Null }
            $peers = [int]$cfg.hotspot.registryPeers
            New-ItemProperty -Path $regPath -Name "WifiMaxPeers" -Value $peers -PropertyType DWord -Force | Out-Null
            Log "WifiMaxPeers = $peers"
            Restart-Icssvc
            Log "icssvc 已重启"
        }

        & $agent --install-task 2>&1 | ForEach-Object { Log ("  " + $_) }

        Log "启动守护"
        Start-Process -FilePath $agent -WorkingDirectory $InstallDir -WindowStyle Hidden
        Start-Sleep -Seconds 3

        $status = (& $agent --status) -join ""
        Log ("状态：" + $status)
        Log ("面板：http://127.0.0.1:" + $cfg.port + "/?t=" + $cfg.token)
    }

    "uninstall" {
        Log "关闭热点并停止守护"
        if (Test-Path $agent) { & $agent --off 2>&1 | Out-Null }
        Stop-Agent

        Log "删除登录自启任务"
        & schtasks.exe /Delete /TN "ClassroomKit-Agent" /F 2>&1 | ForEach-Object { Log ("  " + $_) }

        if (-not $SkipRegistry) {
            Log "恢复 WifiMaxPeers（删除自定义值）"
            if (Get-ItemProperty -Path $regPath -Name WifiMaxPeers -ErrorAction SilentlyContinue) {
                Remove-ItemProperty -Path $regPath -Name WifiMaxPeers -Force -ErrorAction SilentlyContinue
                Restart-Icssvc
                Log "icssvc 已重启"
            } else {
                Log "没有自定义值，跳过"
            }
        }

        Log "清理强力卸载工具目录"
        $bcuDir = Join-Path $InstallDir "BCU"
        if (Test-Path $bcuDir) { Remove-Item $bcuDir -Recurse -Force -ErrorAction SilentlyContinue }

        if (-not $KeepData) {
            Log "删除数据目录 $dataDir"
            Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Log "完成"
exit 0
