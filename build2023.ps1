# build2023.ps1 - builds DAN_Plugin.dll for Revit 2023 (net48).
# Requires the real Visual Studio Build Tools MSBuild (framework-hosted) - the
# .NET SDK's own MSBuild (dotnet build) cannot compile this project's WPF/XAML
# (Form.xaml, ElevationTag.xaml, NotesWindow.xaml, StairLandingExitsWindow.xaml):
# MarkupCompilePass1/2 come from PresentationBuildTasks, a .NET-Framework-only
# task assembly that cannot be hosted inside the .NET Core/8/10 MSBuild process.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File build2023.ps1              # Debug
#   powershell -ExecutionPolicy Bypass -File build2023.ps1 -Config Release

param(
    [string]$Config = "Debug"
)

$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    Write-Error "MSBuild.exe not found at $msbuild - is Build Tools for Visual Studio (.NET desktop development workload) installed?"
    exit 1
}

Set-Location $PSScriptRoot
& $msbuild "DAN_Plugin.csproj" /t:Restore /p:Configuration=$Config /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $msbuild "DAN_Plugin.csproj" /t:Build /p:Configuration=$Config /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dll = Join-Path $PSScriptRoot "bin\$Config\DAN_Plugin.dll"
Write-Host ""
Write-Host "Done: $dll"
