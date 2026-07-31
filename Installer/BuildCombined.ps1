# BuildCombined.ps1 — собирает DAN_Plugin/Loader для Revit 2023 (net48, MSBuild) и
# Revit 2026 (.NET 8, dotnet build) в Release, затем компилирует ОДИН установщик
# (DAN_Plugin_Combined.iss), который на целевой машине сам определяет, какие версии
# Revit установлены, и разворачивает плагин только для них.
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root    = Split-Path $PSScriptRoot -Parent
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
$iscc    = if (Test-Path "C:\Program Files (x86)\Inno Setup 6\ISCC.exe") {
               "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
           } else {
               "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
           }
$issFile = "$PSScriptRoot\DAN_Plugin_Combined.iss"

# ── 1. Inno Setup ────────────────────────────────────────────────────────────
if (-not (Test-Path $iscc)) {
    Write-Host "Inno Setup не найден. Устанавливаю через winget..." -ForegroundColor Yellow
    winget install --id JRSoftware.InnoSetup --silent --accept-package-agreements --accept-source-agreements
    if (-not (Test-Path $iscc)) {
        Write-Error "Не удалось найти ISCC.exe после установки. Установите Inno Setup вручную: https://jrsoftware.org/isdl.php"
    }
    Write-Host "Inno Setup установлен." -ForegroundColor Green
}

# ── 2. Сборка версии для Revit 2023 (Release) ────────────────────────────────
Write-Host "`nСборка DAN_Plugin для Revit 2023 (Release)..." -ForegroundColor Cyan
& $msbuild "$root\DAN_Plugin.sln" /p:Configuration=Release /t:Build /v:minimal
if ($LASTEXITCODE -ne 0) { Write-Error "MSBuild (2023) завершился с ошибкой." }
Write-Host "Сборка 2023 успешна." -ForegroundColor Green

# ── 3. Сборка версии для Revit 2026 (Release) ────────────────────────────────
Write-Host "`nСборка DAN_Plugin2026 (Release)..." -ForegroundColor Cyan
& dotnet build "$root\DAN_Plugin2026.csproj" -c Release
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet build DAN_Plugin2026.csproj завершился с ошибкой." }

Write-Host "`nСборка Loader2026 (Release)..." -ForegroundColor Cyan
& dotnet build "$root\Loader2026.csproj" -c Release
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet build Loader2026.csproj завершился с ошибкой." }
Write-Host "Сборка 2026 успешна." -ForegroundColor Green

# ── 4. Компиляция комбинированного установщика ───────────────────────────────
Write-Host "`nКомпиляция комбинированного установщика..." -ForegroundColor Cyan
& $iscc $issFile
if ($LASTEXITCODE -ne 0) { Write-Error "ISCC.exe завершился с ошибкой." }

$output = "$PSScriptRoot\Output"
$exe    = Get-ChildItem $output -Filter "DAN_Plugin_Setup_*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "`nУстановщик готов:" -ForegroundColor Green
Write-Host "  $($exe.FullName)" -ForegroundColor White

# Открыть папку с результатом
Start-Process explorer.exe $output
