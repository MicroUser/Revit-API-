# BuildCombinedLD.ps1 — тот же установщик, что BuildCombined.ps1 (тот же AppId/путь
# установки/manifest — заменяет обычную установку DAN_Plugin_Setup на этой машине), но
# Loader.dll собирается в конфигурации Release-LD (LD_BRAND, см. Loader.cs) — единственное
# видимое отличие: вкладка ленты Revit называется "LD", а не "DAN". DAN_Plugin.dll и всё
# остальное — из обычной сборки Release, общей с DAN_Plugin_Combined.iss.
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root    = Split-Path $PSScriptRoot -Parent
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
$iscc    = if (Test-Path "C:\Program Files (x86)\Inno Setup 6\ISCC.exe") {
               "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
           } else {
               "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
           }
$issFile = "$PSScriptRoot\DAN_Plugin_Combined_LD.iss"

# ── 1. Inno Setup ────────────────────────────────────────────────────────────
if (-not (Test-Path $iscc)) {
    Write-Host "Inno Setup не найден. Устанавливаю через winget..." -ForegroundColor Yellow
    winget install --id JRSoftware.InnoSetup --silent --accept-package-agreements --accept-source-agreements
    if (-not (Test-Path $iscc)) {
        Write-Error "Не удалось найти ISCC.exe после установки. Установите Inno Setup вручную: https://jrsoftware.org/isdl.php"
    }
    Write-Host "Inno Setup установлен." -ForegroundColor Green
}

# ── 2. DAN_Plugin для Revit 2023 (Release, общий/небрендированный) ───────────
Write-Host "`nСборка DAN_Plugin для Revit 2023 (Release)..." -ForegroundColor Cyan
& $msbuild "$root\DAN_Plugin.sln" /p:Configuration=Release /t:Build /v:minimal
if ($LASTEXITCODE -ne 0) { Write-Error "MSBuild (2023, DAN_Plugin.sln) завершился с ошибкой." }
Write-Host "Сборка DAN_Plugin 2023 успешна." -ForegroundColor Green

# ── 3. Loader с вкладкой "LD" для Revit 2023 (напрямую по csproj, не через .sln —
#      у DAN_Plugin.csproj нет конфигурации Release-LD) ──────────────────────
Write-Host "`nСборка Loader (Release-LD, вкладка ""LD"") для Revit 2023..." -ForegroundColor Cyan
& $msbuild "$root\Loader.csproj" /p:Configuration=Release-LD /t:Build /v:minimal
if ($LASTEXITCODE -ne 0) { Write-Error "MSBuild (2023, Loader.csproj Release-LD) завершился с ошибкой." }
Write-Host "Сборка Loader-LD 2023 успешна." -ForegroundColor Green

# ── 4. DAN_Plugin2026 (Release, общий/небрендированный) ──────────────────────
Write-Host "`nСборка DAN_Plugin2026 (Release)..." -ForegroundColor Cyan
& dotnet build "$root\DAN_Plugin2026.csproj" -c Release
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet build DAN_Plugin2026.csproj завершился с ошибкой." }

# ── 5. Loader2026 с вкладкой "LD" ─────────────────────────────────────────────
Write-Host "`nСборка Loader2026 (Release-LD, вкладка ""LD"")..." -ForegroundColor Cyan
& dotnet build "$root\Loader2026.csproj" -c Release-LD
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet build Loader2026.csproj (Release-LD) завершился с ошибкой." }
Write-Host "Сборка 2026 (LD) успешна." -ForegroundColor Green

# ── 6. Компиляция LD-установщика ──────────────────────────────────────────────
Write-Host "`nКомпиляция LD-установщика..." -ForegroundColor Cyan
& $iscc $issFile
if ($LASTEXITCODE -ne 0) { Write-Error "ISCC.exe завершился с ошибкой." }

$output = "$PSScriptRoot\Output"
$exe    = Get-ChildItem $output -Filter "LD_Plugin_Setup_*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "`nУстановщик готов:" -ForegroundColor Green
Write-Host "  $($exe.FullName)" -ForegroundColor White

# Открыть папку с результатом
Start-Process explorer.exe $output
