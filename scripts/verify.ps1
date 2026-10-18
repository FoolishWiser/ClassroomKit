<#
.SYNOPSIS
  全功能验收：装完之后跑一遍，逐条打 OK/FAIL，最后给汇总。
.DESCRIPTION
  只依赖程序自身（HTTP API + CLI + 注册表 + 计划任务），不改配置（改完会还原）。
  用法：powershell -ExecutionPolicy Bypass -File verify.ps1
        powershell -ExecutionPolicy Bypass -File verify.ps1 -SkipRestart   # 跳过重启令牌持久性测试
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files\ClassroomKit",
    [string]$ReportPath = "",
    [switch]$SkipRestart
)

$ErrorActionPreference = "Continue"
$script:results = New-Object System.Collections.ArrayList
$script:t0 = Get-Date

function Add-Result([string]$name, [bool]$ok, [string]$detail = "") {
    [void]$script:results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
    $tag = if ($ok) { "OK  " } else { "FAIL" }
    Write-Host ("[{0}] {1}{2}" -f $tag, $name, $(if ($detail) { "  -- $detail" } else { "" }))
}

function Test-Name([string]$name, [scriptblock]$body) {
    try {
        $r = & $body
        if ($r -is [array]) { Add-Result $name ([bool]$r[0]) $r[1] }
        else { Add-Result $name ([bool]$r) "" }
    } catch {
        Add-Result $name $false $_.Exception.Message
    }
}

function Wait-Until([scriptblock]$cond, [int]$seconds = 40) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $seconds) {
        try { if (& $cond) { return $true } } catch { }
        Start-Sleep -Milliseconds 700
    }
    return $false
}

# ---------- 基础位置 ----------
$exe = Join-Path $InstallDir "ClassroomKit.Agent.exe"
$dataDir = Join-Path $env:ProgramData "ClassroomKit"
$configPath = Join-Path $dataDir "config.json"
if (-not $ReportPath) { $ReportPath = Join-Path $env:TEMP "ck-verify.txt" }

Write-Host "=== ClassroomKit 全功能验收 ===" -ForegroundColor Cyan
Write-Host "安装目录：$InstallDir"
Write-Host ""

if (-not (Test-Path $exe)) { Add-Result "程序存在" $false "找不到 $exe"; }
else { Add-Result "程序存在" $true }

$cfg = $null
try { $cfg = Get-Content $configPath -Raw | ConvertFrom-Json } catch { }
Add-Result "配置文件可读" ([bool]$cfg) $configPath

if (-not $cfg) {
    Write-Host "配置缺失，后续 API 测试跳过" -ForegroundColor Red
} else {
    $token = $cfg.token
    $base = "http://127.0.0.1:$($cfg.port)"

    # 拼 URL：path 自己可能带查询串（如 /api/logs?take=20），要把令牌接在后面
    function Url([string]$path) {
        if ($path.Contains("?")) { return "$base$($path)&t=$token" }
        return "$base$($path)?t=$token"
    }

    function Api([string]$path, [string]$method = "GET", $body = $null) {
        if ($body) {
            return Invoke-RestMethod -Uri (Url $path) -Method $method -ContentType "application/json" `
                -Body ([Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Compress -Depth 5))) -TimeoutSec 10
        }
        return Invoke-RestMethod -Uri (Url $path) -Method $method -TimeoutSec 10
    }

    function State() { return Api "/api/state" }

    # 直接调用程序本体并可靠地拿到标准输出（GUI 子系统进程用 & 调用时 PowerShell 不捕获其 stdout）
    function Run-Cli([string]$argLine) {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $exe
        $psi.Arguments = $argLine
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
        $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
        $p = [System.Diagnostics.Process]::Start($psi)
        $out = $p.StandardOutput.ReadToEnd()
        $p.WaitForExit(30000) | Out-Null
        return $out
    }

    # ---------- 文件完整性 ----------
    foreach ($f in @("web\index.html", "web\app.js", "web\style.css")) {
        $p = Join-Path $InstallDir $f
        Add-Result "文件：$f" (Test-Path $p)
    }

    # ---------- CLI ----------
    Test-Name "--version 输出 0.1.0" {
        $v = (Run-Cli "--version") -join " "
        return @(($v -match "0\.1\.0"), $v.Trim())
    }
    Test-Name "--status 是合法 JSON 且字段齐全" {
        $s = (Run-Cli "--status") -join ""
        $o = $s | ConvertFrom-Json
        $need = @("version", "guardState", "hotspot", "uplink", "restartCount", "bcu")
        $missing = $need | Where-Object { -not ($o.PSObject.Properties.Name -contains $_) }
        return @(($missing.Count -eq 0), "缺失=$($missing -join ',')")
    }

    # ---------- 鉴权 ----------
    Test-Name "无令牌返回 401" {
        try { Invoke-WebRequest -Uri "$base/api/state" -TimeoutSec 5 | Out-Null; return @($false, "居然成功了") }
        catch { return @(($_.Exception.Response.StatusCode.value__ -eq 401), "code=$($_.Exception.Response.StatusCode.value__)") }
    }
    Test-Name "错令牌返回 401" {
        try { Invoke-WebRequest -Uri "$base/api/state?t=deadbeef" -TimeoutSec 5 | Out-Null; return @($false, "居然成功了") }
        catch { return @(($_.Exception.Response.StatusCode.value__ -eq 401), "code=$($_.Exception.Response.StatusCode.value__)") }
    }

    # ---------- API 读接口 ----------
    Test-Name "GET /api/health" { $h = Api "/api/health"; return @($h.ok -eq $true, "version=$($h.version)") }
    Test-Name "GET /api/state 字段齐全" {
        $s = State
        $need = @("state", "ssid", "passphrase", "clients", "maxClients", "targetClients", "registryPeers", "allowWifiUplink", "noConnectionsTimeout", "currentSsid")
        $missing = $need | Where-Object { -not ($s.hotspot.PSObject.Properties.Name -contains $_) }
        return @(($missing.Count -eq 0), "缺失=$($missing -join ',')")
    }
    Test-Name "GET /api/clients" { $c = Api "/api/clients"; return @(($null -ne $c.count -and $null -ne $c.targetClients), "count=$($c.count)/$($c.targetClients)") }
    Test-Name "GET /api/logs?take=20 有内容" { $l = Api "/api/logs?take=20"; return @(($l.lines.Count -gt 0), "行数=$($l.lines.Count)") }
    Test-Name "静态面板首页可访问" {
        $r = Invoke-WebRequest -Uri (Url "/") -TimeoutSec 10
        return @(($r.StatusCode -eq 200 -and $r.Content -match "ClassroomKit"), "len=$($r.Content.Length)")
    }
    Test-Name "路径穿越被拒（%2e%2e 直接送到服务端）" {
        # 用 curl --path-as-is，避免 .NET 客户端提前把 .. 规范化掉
        $code = (& curl.exe -s -o NUL -w "%{http_code}" --path-as-is "$base/web/../../config.json?t=$token")
        return @(($code -eq "403" -or $code -eq "404"), "code=$code")
    }

    # ---------- 系统上限与自动关 ----------
    Test-Name "注册表 WifiMaxPeers == 配置值" {
        $v = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\icssvc\Settings" -Name WifiMaxPeers -ErrorAction Stop).WifiMaxPeers
        return @(($v -eq $cfg.hotspot.registryPeers), "注册表=$v 配置=$($cfg.hotspot.registryPeers)")
    }
    Test-Name "热点系统上限 ≥ 16" {
        $s = State
        return @(($s.hotspot.maxClients -ge 16), "maxClients=$($s.hotspot.maxClients)")
    }
    Test-Name "无设备自动关已被关掉" {
        $s = State
        return @(($s.hotspot.noConnectionsTimeout -eq $false), "noConnectionsTimeout=$($s.hotspot.noConnectionsTimeout)")
    }

    # ---------- 策略 / 守护 ----------
    Test-Name "初始状态热点在运行" {
        & $exe --on | Out-Null
        $ok = Wait-Until { (State).hotspot.state -eq "On" } 30
        return @($ok, "state=$((State).hotspot.state)")
    }
    Test-Name "策略关 → 守护把热点关掉" {
        Api "/api/policy" "POST" @{ enabled = $false } | Out-Null
        $ok = Wait-Until { (State).hotspot.state -ne "On" } 40
        return @($ok, "state=$((State).hotspot.state)")
    }
    Test-Name "策略开 → 热点自动回来" {
        Api "/api/policy" "POST" @{ enabled = $true } | Out-Null
        $ok = Wait-Until { (State).hotspot.state -eq "On" } 40
        return @($ok, "state=$((State).hotspot.state)")
    }
    Test-Name "action=stop 立即关" {
        Api "/api/action" "POST" @{ action = "stop" } | Out-Null
        $ok = Wait-Until { (State).hotspot.state -ne "On" } 30
        return @($ok, "state=$((State).hotspot.state)")
    }
    Test-Name "action=start 立即开" {
        Api "/api/action" "POST" @{ action = "start" } | Out-Null
        $ok = Wait-Until { (State).hotspot.state -eq "On" } 30
        return @($ok, "state=$((State).hotspot.state)")
    }
    Test-Name "action=ensure 幂等" {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Api "/api/action" "POST" @{ action = "ensure" } | Out-Null
        $sw.Stop()
        return @(($sw.Elapsed.TotalSeconds -lt 5), "耗时 $([Math]::Round($sw.Elapsed.TotalSeconds,2))s")
    }

    # ---------- 设置 ----------
    $origSsid = $cfg.hotspot.ssid
    $origTarget = $cfg.hotspot.targetClients
    Test-Name "改 SSID 生效并落盘" {
        $new = "CK-TEST-" + (Get-Random -Minimum 100 -Maximum 999)
        Api "/api/settings" "POST" @{ ssid = $new; passphrase = $cfg.hotspot.passphrase; targetClients = $origTarget; allowWifiUplink = $false } | Out-Null
        $s = State
        $onDisk = (Get-Content $configPath -Raw | ConvertFrom-Json).hotspot.ssid
        $ok = ($s.hotspot.ssid -eq $new -and $onDisk -eq $new)
        Api "/api/settings" "POST" @{ ssid = $origSsid; passphrase = $cfg.hotspot.passphrase; targetClients = $origTarget; allowWifiUplink = $false } | Out-Null
        return @($ok, "设 $new → 回读 $($s.hotspot.ssid) / 磁盘 $onDisk（已还原）")
    }
    Test-Name "非法设置被拒（密码太短）" {
        try { Api "/api/settings" "POST" @{ passphrase = "123" } | Out-Null; return @($false, "居然接受了") }
        catch { return @(($_.Exception.Response.StatusCode.value__ -eq 400), "code=$($_.Exception.Response.StatusCode.value__)") }
    }
    Test-Name "非法设置被拒（目标 0 台）" {
        try { Api "/api/settings" "POST" @{ targetClients = 0 } | Out-Null; return @($false, "居然接受了") }
        catch { return @(($_.Exception.Response.StatusCode.value__ -eq 400), "code=$($_.Exception.Response.StatusCode.value__)") }
    }

    # ---------- 计划任务 / 进程 ----------
    Test-Name "登录自启任务已注册且指向本体" {
        $out = (& schtasks.exe /Query /TN "ClassroomKit-Agent" /XML 2>&1) -join "`n"
        return @(($out -match "ClassroomKit.Agent.exe"), $(if ($out -match "LogonTrigger") { "登录触发" } else { "无 LogonTrigger?" }))
    }
    Test-Name "守护进程在运行" {
        $p = Get-Process ClassroomKit.Agent -ErrorAction SilentlyContinue
        return @([bool]$p, "PID=$(($p | Select-Object -First 1).Id)")
    }

    # ---------- 重启后持久性 ----------
    if (-not $SkipRestart) {
        Test-Name "重启守护：令牌与设置不变" {
            $before = (Get-Content $configPath -Raw | ConvertFrom-Json)
            Get-Process ClassroomKit.Agent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
            Start-Process -FilePath $exe -ArgumentList "--run" -WorkingDirectory $InstallDir -WindowStyle Hidden
            $up = Wait-Until { try { (Api "/api/health").ok -eq $true } catch { $false } } 30
            $after = (Get-Content $configPath -Raw | ConvertFrom-Json)
            $ok = ($up -and $after.token -eq $before.token -and $after.hotspot.ssid -eq $before.hotspot.ssid)
            return @($ok, "token一致=$($after.token -eq $before.token) 启动=$up")
        }
        Test-Name "重启后热点自动回来" {
            $ok = Wait-Until { (State).hotspot.state -eq "On" } 40
            return @($ok, "state=$((State).hotspot.state)")
        }
    }

    # ---------- BCU ----------
    $bcuExe = Join-Path $InstallDir "BCU\BCUninstaller.exe"
    if (Test-Path $bcuExe) {
        Test-Name "强力卸载：拉起 BCU（返回 ok）" {
            $r = Api "/api/action" "POST" @{ action = "bcu" }
            return @(($r.ok -eq $true), $r.message)
        }
    } else {
        Add-Result "强力卸载：BCU 未安装（可选，不影响主功能）" $true "用 get-bcu.ps1 可补装"
    }
}

# ---------- 汇总 ----------
$total = $script:results.Count
$pass = ($script:results | Where-Object { $_.Ok }).Count
$fail = $total - $pass
Write-Host ""
Write-Host ("汇总：{0}/{1} 通过，{2} 失败，用时 {3:N1}s" -f $pass, $total, $fail, ((Get-Date) - $script:t0).TotalSeconds) -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })

$lines = @()
$lines += "ClassroomKit 验收报告 " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
$lines += "安装目录：$InstallDir"
$lines += ""
foreach ($r in $script:results) {
    $lines += ("[{0}] {1}{2}" -f $(if ($r.Ok) { "OK  " } else { "FAIL" }), $r.Name, $(if ($r.Detail) { "  -- " + $r.Detail } else { "" }))
}
$lines += ""
$lines += ("汇总：{0}/{1} 通过，{2} 失败" -f $pass, $total, $fail)
[IO.File]::WriteAllLines($ReportPath, $lines, (New-Object Text.UTF8Encoding($false)))
Write-Host "报告：$ReportPath" -ForegroundColor DarkGray

if ($fail -gt 0) { exit 1 } else { exit 0 }
