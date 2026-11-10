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
foreach ($d in $subDirs) {
    $id = "dir_" + (Get-SafeId $d)
    $dirIds[$d] = $id
    [void]$sb.AppendLine(('      <Directory Id="{0}" Name="{1}" />' -f $id, $d))
}
[void]$sb.AppendLine('    </DirectoryRef>')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('    <ComponentGroup Id="Files">')

foreach ($f in $files) {
    $rel = $f.FullName.Substring($Dist.Length).TrimStart('\')
    $parts = $rel -split '\\'
    $dirId = 'INSTALLFOLDER'
    if ($parts.Count -gt 1) {
        $top = $parts[0]
        if (-not $dirIds.ContainsKey($top)) { $dirIds[$top] = "dir_" + (Get-SafeId $top) }
        $dirId = $dirIds[$top]
    }
    $compId = "c_" + (Get-SafeId $rel)
    $fileId = "f_" + (Get-SafeId $rel)
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
Write-Host "    版本：$Version"
Write-Host "    ProductCode：$productCode"
Write-Host "    输出：$Out"

if (Test-Path $Out) { Remove-Item $Out -Force }

# ProductCode 由版本号算出：同一版本重建得到同一个 ProductCode（Windows 会把它当"同一产品"替换，
# 不会在「应用和功能」里留下两行残留），不同版本则是不同产品（配合 MajorUpgrade 走升级）。
$productCode = Get-StableGuid "ClassroomKit/ProductCode/$Version"

& $Wix build $wxs $filesWxs -arch x64 -d "DistDir=$Dist" -d "ProductVersion=$Version" -d "ProductCode=$productCode" -o $Out
if ($LASTEXITCODE -ne 0) { throw "wix build 失败（退出码 $LASTEXITCODE）" }

$item = Get-Item $Out
Write-Host ""
Write-Host ("完成：{0}  {1:N1} MB" -f $item.Name, ($item.Length / 1MB)) -ForegroundColor Green
Write-Host ("sha256：" + (Get-FileHash $Out -Algorithm SHA256).Hash.ToLowerInvariant())
Write-Host ""
Write-Host "安装：msiexec /i `"$($item.FullName)`"        （静默：加 /qn）" -ForegroundColor Gray
Write-Host "卸载：msiexec /x `"$($item.FullName)`"" -ForegroundColor Gray
