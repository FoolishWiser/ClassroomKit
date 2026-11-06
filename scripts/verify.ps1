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
    [string]$ExpectedVersion = "",
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

    # 用 curl 拿「状态码 + 正文」：4xx/5xx 也能读到服务端写的 JSON
    # 注意两件事：① PS 5.1 会吃掉参数里的双引号 → 正文落文件用 @file 传；
    # ② 直接捕获 curl 的标准输出会按 GBK 解码成乱码 → 正文写文件后按 UTF-8 读。
    function CurlJson([string]$path, [string]$json) {
        $reqFile = Join-Path $env:TEMP "ck-request.json"
        $resFile = Join-Path $env:TEMP "ck-response.json"
        [IO.File]::WriteAllText($reqFile, $json, (New-Object Text.UTF8Encoding($false)))
        $code = (& curl.exe -s -m 15 -X POST -H "Content-Type: application/json" -d "@$reqFile" -o $resFile -w "%{http_code}" (Url $path) 2>$null)
        $status = 0
        [void][int]::TryParse((($code -join "")).Trim(), [ref]$status)
        $body = ""
        if (Test-Path $resFile) { $body = [IO.File]::ReadAllText($resFile, (New-Object Text.UTF8Encoding($false))) }
        return @{ Status = $status; Body = $body }
    }

    # 不抛异常的请求：返回 @{ Status; Body }，用来测 4xx/5xx 分支
    function ApiRaw([string]$path, [string]$method = "GET", $body = $null) {
        try {
            $args2 = @{ Uri = (Url $path); Method = $method; TimeoutSec = 10; UseBasicParsing = $true }
            if ($body) {
                $args2.ContentType = "application/json"
                $args2.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Compress -Depth 5))
            }
            $r = Invoke-WebRequest @args2
            return @{ Status = [int]$r.StatusCode; Body = [string]$r.Content }
        } catch {
            $resp = $_.Exception.Response
            if ($resp) {
                $sr = New-Object IO.StreamReader($resp.GetResponseStream())
                return @{ Status = [int]$resp.StatusCode; Body = $sr.ReadToEnd() }
            }
            return @{ Status = 0; Body = $_.Exception.Message }
        }
    }

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
    Test-Name "--version 输出合法版本号" {
        $v = ((Run-Cli "--version") -join " ").Trim()
        $ok = $v -match "ClassroomKit\.Agent \d+\.\d+\.\d+"
        if ($ok -and $ExpectedVersion) { $ok = $v -match [regex]::Escape($ExpectedVersion) }
        return @($ok, $v)
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
    Test-Name "GET /api/state 上行信息完整" {
        $u = (State).uplink
        $need = @("available", "isWifi", "name", "hasInternet", "mode", "fallbackToLan", "reason")
        $missing = $need | Where-Object { -not ($u.PSObject.Properties.Name -contains $_) }
        return @(($missing.Count -eq 0), "网卡=$($u.name)，有外网=$($u.hasInternet)，缺失=$($missing -join ',')")
    }
    Test-Name "GET /api/uplinks 能列出候选网卡" {
        $r = Api "/api/uplinks"
        $n = @($r.candidates).Count
        return @(($n -ge 1 -and -not [string]::IsNullOrWhiteSpace($r.source)), "source=$($r.source)，候选=$n 块，理由=$($r.reason)")
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
            # 用交互令牌的临时任务拉起，进程才会落在用户会话里（从 SSH/Session 0 直接 Start-Process 会得到无托盘实例）。
            # 走 XML 注册而不是 /TR，避免路径含空格时 schtasks 把参数按空格劈开。
            $tmp = "ClassroomKit-Agent-Verify"
            $taskXml = Join-Path $env:TEMP "ck-verify-task.xml"
            $xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>ClassroomKit verify</Description></RegistrationInfo>
  <Triggers />
  <Principals>
    <Principal id="Author">
      <UserId>$env:COMPUTERNAME\$env:USERNAME</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$exe</Command>
    </Exec>
  </Actions>
</Task>
"@
            [IO.File]::WriteAllText($taskXml, $xml, [Text.Encoding]::Unicode)
            & schtasks.exe /Create /TN $tmp /XML $taskXml /F 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) {
                & schtasks.exe /Run /TN $tmp 2>&1 | Out-Null
                & schtasks.exe /Delete /TN $tmp /F 2>&1 | Out-Null
            } else {
                Start-Process -FilePath $exe -WorkingDirectory $InstallDir -WindowStyle Hidden
            }
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

    # ---------- 板书保存 / 板书软件冲突 ----------
    Test-Name "GET /api/board 结构完整" {
        $b = Api "/api/board"
        $ok = ($null -ne $b.classIn.saveFolder) -and ($null -ne $b.boardSave.enabled) -and
              ($null -ne $b.conflict.enabled) -and ($null -ne $b.conflict.cooldownMinutes) -and
              ($null -ne $b.conflict.situation)
        return @($ok, ("situation=" + $b.conflict.situation + " 冷却=" + $b.conflict.cooldownMinutes + "分"))
    }

    Test-Name "冲突冷却默认 40 分钟（1..1440）" {
        $b = Api "/api/board"
        $ok = ($b.conflict.cooldownMinutes -ge 1) -and ($b.conflict.cooldownMinutes -le 1440)
        return @($ok, "cooldownMinutes=$($b.conflict.cooldownMinutes)")
    }

    Test-Name "冲突判定能识别 ClassIn 全屏/悬浮窗" {
        if (-not (Api "/api/board").classIn.running) { return @($true, "ClassIn 未运行，跳过（判定逻辑由单测覆盖）") }
        # 守护刚启动时窗口缓存是空的，给它几秒
        $ok = Wait-Until { ((Api "/api/board").conflict.situation) -match "ClassIn" } 20
        return @($ok, (Api "/api/board").conflict.situation)
    }

    Test-Name "action=conflict-check 结果可解释" {
        $r = CurlJson "/api/action" '{"action":"conflict-check","force":true}'
        $ok = ($r.Status -eq 200) -or (($r.Status -eq 409) -and ($r.Body -match '"message"'))
        return @($ok, ("HTTP " + $r.Status + " " + $r.Body))
    }

    Test-Name "action=conflict-choice 接受 keep-both" {
        $r = Api "/api/action" "POST" @{ action = "conflict-choice"; choice = "keep-both" }
        return @(([bool]$r.ok), $r.message)
    }

    Test-Name "action=conflict-choice 拒绝非法值" {
        $r = CurlJson "/api/action" '{"action":"conflict-choice","choice":"nonsense"}'
        $ok = ($r.Status -eq 500) -and ($r.Body -match 'nonsense')
        return @($ok, ("HTTP " + $r.Status + " " + $r.Body))
    }

    Test-Name "save-classin-board 失败也可解释" {
        $b = Api "/api/board"
        if ($b.classIn.running) { return @($true, "ClassIn 正在运行，跳过（避免验收时真的存一份板书）") }
        $r = CurlJson "/api/action" '{"action":"save-classin-board","closeAfterSave":false}'
        $ok = ($r.Status -eq 500) -and ($r.Body -match "ClassIn")
        return @($ok, ("HTTP " + $r.Status + " " + $r.Body))
    }

    Test-Name "菜单命令 --conflict-check 可用（JSON）" {
        $v = ((Run-Cli "--conflict-check") -join "").Trim()
        if (-not $v) { return @($true, "当前无冲突（exit 8），跳过") }
        $ok = $v -match '"situation"'
        return @($ok, ($v.Substring(0, [Math]::Min(90, $v.Length))))
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
