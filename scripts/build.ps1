<#
.SYNOPSIS
  在 Windows 上构建单文件发布包（需要 .NET 8 SDK）。
#>
[CmdletBinding()]
param(
    [string]$Output = "dist"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Definition)
$outDir = Join-Path $repo $Output

Push-Location $repo
try {
    $dotnetArgs = @(
        "publish", "src/ClassroomKit.Agent/ClassroomKit.Agent.csproj",
        "-c", "Release", "-r", "win-x64", "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-o", $outDir
    )
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（$LASTEXITCODE）" }

    Copy-Item -Path (Join-Path $repo "scripts\*.ps1") -Destination $outDir -Force
    Copy-Item -Path (Join-Path $repo "docs\*") -Destination $outDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "构建完成：$outDir" -ForegroundColor Green
} finally {
    Pop-Location
}
