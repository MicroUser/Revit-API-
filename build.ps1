# build.ps1 - builds DAN_Plugin.dll for Revit 2023 (net48) and/or Revit 2026 (net10.0-windows).
# Replaces the old build2023.ps1/build2026.ps1 - same two toolchains, one shared function.
#
# Revit 2023 requires the real Visual Studio Build Tools MSBuild (framework-hosted) - the
# .NET SDK's own MSBuild (dotnet build) cannot compile this project's WPF/XAML (Form.xaml,
# ElevationTag.xaml, NotesWindow.xaml, StairLandingExitsWindow.xaml): MarkupCompilePass1/2
# come from PresentationBuildTasks, a .NET-Framework-only task assembly that cannot be
# hosted inside the .NET Core/8/10 MSBuild process.
# Revit 2026 uses the user-local .NET 10 SDK installed under %LOCALAPPDATA%\Microsoft\dotnet
# (separate from the system-wide C:\Program Files\dotnet, which only has the runtime).
#
# Defaults to Release for BOTH targets - Installer\Publish.ps1 reads from bin\Release/
# bin2026\Release, so a plain "build.ps1" with no args leaves them ready to publish.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File build.ps1                       # both, Release
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Target 2023
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Target 2026 -Config Debug

param(
    [ValidateSet("2023", "2026", "All")]
    [string]$Target = "All",
    [string]$Config = "Release"
)

Set-Location $PSScriptRoot

function Build-Plugin {
    param(
        [string]$Csproj,
        [ValidateSet("msbuild", "dotnet")]
        [string]$Tool,
        [string]$OutDir,
        [string]$Config
    )

    if ($Tool -eq "msbuild") {
        $msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe"
        if (-not (Test-Path $msbuild)) {
            Write-Error "MSBuild.exe not found at $msbuild - is Build Tools for Visual Studio (.NET desktop development workload) installed?"
            exit 1
        }
        & $msbuild $Csproj /t:Restore /p:Configuration=$Config /nologo /v:minimal
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        & $msbuild $Csproj /t:Build /p:Configuration=$Config /nologo /v:minimal
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    else {
        $dotnetDir = "$env:LOCALAPPDATA\Microsoft\dotnet"
        if (-not (Test-Path "$dotnetDir\dotnet.exe")) {
            Write-Error "dotnet.exe not found under $dotnetDir"
            exit 1
        }
        $env:PATH = "$dotnetDir;" + $env:PATH
        $env:DOTNET_ROOT = $dotnetDir
        & "$dotnetDir\dotnet.exe" build $Csproj -c $Config -v minimal
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $dll = Join-Path $PSScriptRoot "$OutDir\$Config\DAN_Plugin.dll"
    Write-Host ""
    Write-Host "Done: $dll"
}

if ($Target -eq "2023" -or $Target -eq "All") {
    Build-Plugin -Csproj "DAN_Plugin.csproj" -Tool msbuild -OutDir "bin" -Config $Config
}
if ($Target -eq "2026" -or $Target -eq "All") {
    Build-Plugin -Csproj "DAN_Plugin2026.csproj" -Tool dotnet -OutDir "bin2026" -Config $Config
}
