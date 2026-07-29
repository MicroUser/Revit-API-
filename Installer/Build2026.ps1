# Build2026.ps1 — собирает DAN_Plugin/Loader под Revit 2026 (.NET 8, Release) и создаёт
# установщик через Inno Setup. Параллель Build.ps1 — тот собирает версию для Revit 2023
# через DAN_Plugin.sln; DAN_Plugin2026.csproj/Loader2026.csproj в решение не входят
# (SDK-style, собираются через dotnet build), поэтому здесь отдельный скрипт.
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root    = Split-Path $PSScriptRoot -Parent
$iscc    = if (Test-Path "C:\Program Files (x86)\Inno Setup 6\ISCC.exe") {
               "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
           } else {
               "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
           }
$issFile = "$PSScriptRoot\DAN_Plugin2026.iss"

# ── 1. Inno Setup ────────────────────────────────────────────────────────────
if (-not (Test-Path $iscc)) {
    Write-Host "Inno Setup не найден. Устанавливаю через winget..." -ForegroundColor Yellow
    winget install --id JRSoftware.InnoSetup --silent --accept-package-agreements --accept-source-agreements
    if (-not (Test-Path $iscc)) {
        Write-Error "Не удалось найти ISCC.exe после установки. Установите Inno Setup вручную: https://jrsoftware.org/isdl.php"
    }
    Write-Host "Inno Setup установлен." -ForegroundColor Green
}

# ── 2. Сборка плагина под Revit 2026 (Release) ───────────────────────────────
Write-Host "`nСборка DAN_Plugin2026 (Release)..." -ForegroundColor Cyan
& dotnet build "$root\DAN_Plugin2026.csproj" -c Release
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet build DAN_Plugin2026.csproj завершился с ошибкой." }

Write-Host "`nСборка Loader2026 (Release)..." -ForegroundColor Cyan
& dotnet build "$root\Loader2026.csproj" -c Release
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet build Loader2026.csproj завершился с ошибкой." }
Write-Host "Сборка успешна." -ForegroundColor Green

# ── 3. Компиляция установщика ────────────────────────────────────────────────
Write-Host "`nКомпиляция установщика..." -ForegroundColor Cyan
& $iscc $issFile
if ($LASTEXITCODE -ne 0) { Write-Error "ISCC.exe завершился с ошибкой." }

$output = "$PSScriptRoot\Output"
$exe    = Get-ChildItem $output -Filter "DAN_Plugin2026_Setup_*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "`nУстановщик готов:" -ForegroundColor Green
Write-Host "  $($exe.FullName)" -ForegroundColor White

# Открыть папку с результатом
Start-Process explorer.exe $output
