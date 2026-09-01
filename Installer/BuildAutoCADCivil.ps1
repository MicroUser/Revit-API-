# BuildAutoCADCivil.ps1 — собирает AutoCAD+Civil.csproj (Release) и компилирует установщик
# (AutoCAD_Civil.iss), который раскладывает бандл в {userappdata}\Autodesk\ApplicationPlugins.
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root    = Split-Path $PSScriptRoot -Parent
$isccCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = $isccCandidates[0] }
$issFile = "$PSScriptRoot\AutoCAD_Civil.iss"

if (-not (Test-Path $iscc)) {
    Write-Host "Inno Setup не найден. Устанавливаю через winget..." -ForegroundColor Yellow
    winget install --id JRSoftware.InnoSetup --silent --accept-package-agreements --accept-source-agreements
    if (-not (Test-Path $iscc)) {
        Write-Error "Не удалось найти ISCC.exe после установки. Установите Inno Setup вручную: https://jrsoftware.org/isdl.php"
    }
}

Write-Host "`nСборка AutoCAD+Civil для AutoCAD 2025 (Release)..." -ForegroundColor Cyan
& dotnet build "$root\AutoCAD+Civil\AutoCAD+Civil.csproj" -c Release
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet build AutoCAD+Civil.csproj завершился с ошибкой." }
Write-Host "Сборка (2025) успешна." -ForegroundColor Green

# Сборка под AutoCAD 2020 — необязательная: требует 4 версионно-специфичные сборки
# (acmgd/acdbmgd/accoremgd/AdWindows) в "C:\Users\User\Downloads\AutoCAD 2020\", их нет по
# умолчанию на этой машине (см. AutoCAD+Civil2020.csproj). Если файлов ещё нет — пропускаем
# без ошибки, установщик просто не включит 2020-вариант (AutoCAD_Civil.iss:
# skipifsourcedoesntexist).
$refDir = "C:\Users\User\Downloads\AutoCAD 2020"
$requiredDlls = "acmgd.dll", "acdbmgd.dll", "accoremgd.dll", "AdWindows.dll"
$missingDlls = @($requiredDlls | Where-Object { -not (Test-Path (Join-Path $refDir $_)) })
if ($missingDlls.Count -eq 0) {
    Write-Host "`nСборка AutoCAD+Civil для AutoCAD 2020 (Release)..." -ForegroundColor Cyan
    & dotnet build "$root\AutoCAD+Civil\AutoCAD+Civil2020.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { Write-Error "dotnet build AutoCAD+Civil2020.csproj завершился с ошибкой." }
    Write-Host "Сборка (2020) успешна." -ForegroundColor Green
} else {
    Write-Host "`nПропускаю сборку под AutoCAD 2020 — не хватает файлов в `"$refDir`": $($missingDlls -join ', ')" -ForegroundColor Yellow
}

Write-Host "`nКомпиляция установщика..." -ForegroundColor Cyan
& $iscc $issFile
if ($LASTEXITCODE -ne 0) { Write-Error "ISCC.exe завершился с ошибкой." }

$output = "$PSScriptRoot\Output"
$exe    = Get-ChildItem $output -Filter "AutoCAD_Civil_Setup_*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "`nУстановщик готов:" -ForegroundColor Green
Write-Host "  $($exe.FullName)" -ForegroundColor White

Start-Process explorer.exe $output
