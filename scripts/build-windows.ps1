# WIN-INSTALLER (issue #4): PowerShell equivalent of
# build-windows.sh for Windows developers. Produces a self-contained
# single-file win-x64 build in `publish\win-x64\`.
#
# Usage (from the `desktop-app\` directory):
#     .\scripts\build-windows.ps1
#
# Prerequisites:
#   * .NET 8 SDK (dotnet --version >= 8.0).
#
# The build is unsigned (see closed #56 — the installer will trigger
# a SmartScreen warning on first run).

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Resolve repo root relative to this script (lives in
# desktop-app\scripts\).
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir

$Project = Join-Path $RepoRoot 'src\MyGame.Desktop\MyGame.Desktop.csproj'
$OutDir  = Join-Path $RepoRoot 'publish\win-x64'

Write-Host "==> Publishing Pathstone for win-x64 (self-contained, single-file)..." -ForegroundColor Cyan
Write-Host "    Project: $Project"
Write-Host "    Output:  $OutDir"
Write-Host ""

dotnet publish $Project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $OutDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host ""
Write-Host "==> Publish complete." -ForegroundColor Green
Write-Host "    Entry point: $OutDir\MyGame.Desktop.exe"
Write-Host ""
Write-Host "Next step: build the installer with NSIS:" -ForegroundColor Cyan
Write-Host "    makensis $(Join-Path $RepoRoot 'installer\pathstone.nsi')"
Write-Host "    (produces $(Join-Path $RepoRoot 'installer\Pathstone-Setup-0.2.0.exe'))"
