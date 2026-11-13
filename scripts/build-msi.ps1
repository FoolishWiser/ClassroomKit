<#
.SYNOPSIS
  用 WiX 把 dist\ 打成 MSI 安装包（扫描 dist 自动生成文件清单，不需要手工维护）。
.DESCRIPTION
  一次性准备：
    dotnet tool install --global wix --version 5.0.2   # 本机已验证 5.0.2（WiX 6/7 需要付费 OSMF 许可，别用）
  然后：
    powershell -File scripts\build-msi.ps1             # 产物 dist\ClassroomKit-0.1.0-x64.msi
    powershell -File scripts\build-msi.ps1 -Version 0.1.1
#>
[CmdletBinding()]
param(
    [string]$Dist = "",
    [string]$Out = "",
    [string]$Version = "0.1.0",
    [string]$Wix = "wix"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
if (-not $Dist) { $Dist = Join-Path $root "dist" }
$Dist = (Resolve-Path $Dist).Path
if (-not $Out) { $Out = Join-Path $Dist ("ClassroomKit-" + $Version + "-x64.msi") }
$wxs = Join-Path $root "installer\product.wxs"
$filesWxs = Join-Path $root "installer\files.wxs"

# MSI 的 ProductVersion 只认纯数字（最多 4 段），预发布后缀必须折成第 4 段：
#   0.3.0-pre.4 -> 0.3.0.4    （同一 X.Y.Z 里 pre.N 递增；1.0 的正式版天然比它大）
#   0.2.11      -> 0.2.11
# 文件名仍保留完整版本号（ClassroomKit-0.3.0-pre.4-x64.msi），只有 MSI 内部的产品版本是数字。
function ConvertTo-MsiVersion([string]$text) {
    $m = [regex]::Match($text, '^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:-(?<tag>[0-9A-Za-z.\-]+))?$')
    if (-not $m.Success) { throw "看不懂版本号：$text（应形如 0.3.0、0.3.0.4 或 0.3.0-pre.4）" }
    $core = "$($m.Groups[1].Value).$($m.Groups[2].Value).$($m.Groups[3].Value)"
    if ($m.Groups[4].Success) { $core = "$core.$($m.Groups[4].Value)" }
    if ($m.Groups['tag'].Success) {
        $pre = [regex]::Match($m.Groups['tag'].Value, '^pre\.(\d+)$')
        if (-not $pre.Success) { throw "预发布后缀目前只支持 pre.N 形式：$text" }
        if ($m.Groups[4].Success) { throw "版本号里不要再同时写第 4 段和 -pre.N：$text" }
        $core = "$core.$([int]$pre.Groups[1].Value)"
    }
    return $core
}

$msiVersion = ConvertTo-MsiVersion $Version

if (-not (Test-Path (Join-Path $Dist "ClassroomKit.Agent.exe"))) {
    throw "dist 里没有程序本体：$Dist。先跑 scripts\rebuild.ps1 或 dotnet publish。"
}
if (-not (Test-Path $wxs)) { throw "找不到 $wxs" }

# wix 可能装在 %USERPROFILE%\.dotnet\tools，PATH 里没有就补上
$cmd = Get-Command $Wix -ErrorAction SilentlyContinue
if (-not $cmd) {
    $candidate = Join-Path $env:USERPROFILE ".dotnet\tools\wix.exe"
    if (Test-Path $candidate) { $Wix = $candidate }
    else { throw "找不到 wix 命令。请先执行：dotnet tool install --global wix --version 5.0.2" }
}

# 由「相对路径」算出稳定的 GUID：同一路径永远同一 GUID，升级时组件身份不乱
function Get-StableGuid([string]$text) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $hash = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text))
    $bytes = New-Object byte[] 16
    [Array]::Copy($hash, $bytes, 16)
    return ([guid]::new($bytes)).ToString().ToUpperInvariant()
}

function Get-SafeId([string]$text) {
    return ($text -replace '[^A-Za-z0-9_]', '_')
}

Write-Host "==> 扫描 $Dist" -ForegroundColor Cyan
$files = Get-ChildItem -Path $Dist -Recurse -File |
    Where-Object { $_.Extension -notin @('.msi', '.wixpdb', '.zip') } |
    Sort-Object FullName
Write-Host ("    共 {0} 个文件" -f $files.Count)

$dirIds = @{}
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
[void]$sb.AppendLine('<!-- 本文件由 scripts/build-msi.ps1 自动生成，不要手改；dist 变了重新生成即可。 -->')
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')

$subDirs = $files | ForEach-Object {
    $rel = $_.FullName.Substring($Dist.Length).TrimStart('\')
    $parts = $rel -split '\\'
    if ($parts.Count -gt 1) { $parts[0] } else { $null }
} | Where-Object { $_ } | Sort-Object -Unique

[void]$sb.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')
$usedDirIds = @{}
foreach ($d in $subDirs) {
    $id = "dir_" + (Get-SafeId $d)
    if ($usedDirIds.ContainsKey($id)) { $id = $id + "_" + (Get-StableGuid ("ClassroomKit/Dir/" + $d.ToLowerInvariant())).Substring(0, 8).ToLowerInvariant() }
    $usedDirIds[$id] = $true
    $dirIds[$d] = $id
    [void]$sb.AppendLine(('      <Directory Id="{0}" Name="{1}" />' -f $id, $d))
}
[void]$sb.AppendLine('    </DirectoryRef>')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('    <ComponentGroup Id="Files">')

$usedIds = @{}
foreach ($f in $files) {
    $rel = $f.FullName.Substring($Dist.Length).TrimStart('\')
    $parts = $rel -split '\\'
    $dirId = 'INSTALLFOLDER'
    if ($parts.Count -gt 1) {
        $top = $parts[0]
        if (-not $dirIds.ContainsKey($top)) { $dirIds[$top] = "dir_" + (Get-SafeId $top) }
        $dirId = $dirIds[$top]
    }
    # 中文文件名（如 原理与限制.md / 部署手册.md）会被规整成一串下划线，可能撞成同一个 Id，
    # 这里给撞车的补一段由相对路径算出的稳定后缀；ClassroomKit.Agent.exe 的 f_ClassroomKit_Agent_exe
    # 在 product.wxs 里被 FileRef 直接引用，它的 Id 天然不会撞，必须保持原样。
    $safe = Get-SafeId $rel
    $compId = "c_$safe"
    $fileId = "f_$safe"
    if ($usedIds.ContainsKey($fileId)) {
        $suffix = (Get-StableGuid ("ClassroomKit/Id/" + $rel.ToLowerInvariant())).Substring(0, 8).ToLowerInvariant()
        $compId = "c_${safe}_$suffix"
        $fileId = "f_${safe}_$suffix"
    }
    $usedIds[$fileId] = $true
    $guid = Get-StableGuid ("ClassroomKit/" + $rel.ToLowerInvariant())
    $src = [System.Security.SecurityElement]::Escape($f.FullName)
    [void]$sb.AppendLine(('      <Component Id="{0}" Directory="{1}" Guid="{2}">' -f $compId, $dirId, $guid))
    [void]$sb.AppendLine(('        <File Id="{0}" Source="{1}" KeyPath="yes" />' -f $fileId, $src))
    [void]$sb.AppendLine('      </Component>')
}

[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')

[System.IO.File]::WriteAllText($filesWxs, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "    已生成 $filesWxs" -ForegroundColor Gray

Write-Host "==> 打包 MSI" -ForegroundColor Cyan
# ProductCode 由完整版本号算出：同一版本重建得到同一个 ProductCode（Windows 会把它当"同一产品"替换，
# 不会在「应用和功能」里留下两行残留），不同版本（含 pre.N 之间）则是不同产品，由 MajorUpgrade 走覆盖升级。
$productCode = Get-StableGuid "ClassroomKit/ProductCode/$Version"

Write-Host "    版本：$Version" -NoNewline
if ($msiVersion -ne $Version) { Write-Host "（MSI 内部产品版本：$msiVersion）" } else { Write-Host "" }
Write-Host "    ProductCode：$productCode"
Write-Host "    输出：$Out"

if (Test-Path $Out) { Remove-Item $Out -Force }

& $Wix build $wxs $filesWxs -arch x64 -d "DistDir=$Dist" -d "ProductVersion=$msiVersion" -d "ProductCode=$productCode" -o $Out
if ($LASTEXITCODE -ne 0) { throw "wix build 失败（退出码 $LASTEXITCODE）" }

$item = Get-Item $Out
Write-Host ""
Write-Host ("完成：{0}  {1:N1} MB" -f $item.Name, ($item.Length / 1MB)) -ForegroundColor Green
Write-Host ("sha256：" + (Get-FileHash $Out -Algorithm SHA256).Hash.ToLowerInvariant())
Write-Host ""
Write-Host "安装：msiexec /i `"$($item.FullName)`"        （静默：加 /qn）" -ForegroundColor Gray
Write-Host "卸载：msiexec /x `"$($item.FullName)`"" -ForegroundColor Gray
