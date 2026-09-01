# build2026.ps1 - builds DAN_Plugin.dll for Revit 2026 (net10.0-windows).
# Uses the user-local .NET 10 SDK installed under %LOCALAPPDATA%\Microsoft\dotnet
# (separate from the system-wide C:\Program Files\dotnet, which only has the runtime).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File build2026.ps1              # Debug
#   powershell -ExecutionPolicy Bypass -File build2026.ps1 -Config Release

param(
    [string]$Config = "Debug"
)

$dotnetDir = "$env:LOCALAPPDATA\Microsoft\dotnet"
if (-not (Test-Path "$dotnetDir\dotnet.exe")) {
    Write-Error "dotnet.exe not found under $dotnetDir"
    exit 1
}
$env:PATH = "$dotnetDir;" + $env:PATH
$env:DOTNET_ROOT = $dotnetDir

Set-Location $PSScriptRoot
& "$dotnetDir\dotnet.exe" build DAN_Plugin2026.csproj -c $Config -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dll = Join-Path $PSScriptRoot "bin2026\$Config\DAN_Plugin.dll"
Write-Host ""
Write-Host "Done: $dll"
