# Build.ps1 — собирает DAN_Plugin (Release) и создаёт установщик через Inno Setup
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root     = Split-Path $PSScriptRoot -Parent
$msbuild  = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
$iscc     = if (Test-Path "C:\Program Files (x86)\Inno Setup 6\ISCC.exe") {
                "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
            } else {
                "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
            }
$issFile  = "$PSScriptRoot\DAN_Plugin.iss"

# ── 1. Inno Setup ────────────────────────────────────────────────────────────
if (-not (Test-Path $iscc)) {
    Write-Host "Inno Setup не найден. Устанавливаю через winget..." -ForegroundColor Yellow
    winget install --id JRSoftware.InnoSetup --silent --accept-package-agreements --accept-source-agreements
    if (-not (Test-Path $iscc)) {
        Write-Error "Не удалось найти ISCC.exe после установки. Установите Inno Setup вручную: https://jrsoftware.org/isdl.php"
    }
    Write-Host "Inno Setup установлен." -ForegroundColor Green
}

# ── 2. Сборка плагина (Release) ──────────────────────────────────────────────
Write-Host "`nСборка DAN_Plugin (Release)..." -ForegroundColor Cyan
& $msbuild "$root\DAN_Plugin.sln" /p:Configuration=Release /t:Build /v:minimal
if ($LASTEXITCODE -ne 0) { Write-Error "MSBuild завершился с ошибкой." }
Write-Host "Сборка успешна." -ForegroundColor Green

# ── 3. Компиляция установщика ────────────────────────────────────────────────
Write-Host "`nКомпиляция установщика..." -ForegroundColor Cyan
& $iscc $issFile
if ($LASTEXITCODE -ne 0) { Write-Error "ISCC.exe завершился с ошибкой." }

$output = "$PSScriptRoot\Output"
$exe    = Get-ChildItem $output -Filter "*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "`nУстановщик готов:" -ForegroundColor Green
Write-Host "  $($exe.FullName)" -ForegroundColor White

# Открыть папку с результатом
Start-Process explorer.exe $output
