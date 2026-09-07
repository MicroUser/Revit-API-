# Publish.ps1 — публикует собранный DAN_Plugin на сетевую шару обновлений
# K:\02_BIM\DAN_Plugin\2023\ (Revit 2023) и/или K:\02_BIM\DAN_Plugin\2026\ (Revit 2026),
# откуда его подхватывают все рабочие станции при следующем запуске Revit (см. UpdateSync
# в Loader.cs). Заменяет старые Publish.ps1/Publish2026.ps1 — та же логика, одна функция.
# Запускать после build.ps1 (или сборки вручную) — здесь используются уже собранные
# bin\Release / bin2026\Release.
#
# Использование:
#   .\Publish.ps1 -Version 1.5.0                  # опубликовать и 2023, и 2026
#   .\Publish.ps1 -Version 1.5.0 -Target 2023     # опубликовать только одну версию
# Без -Version скрипт покажет текущую опубликованную версию (version.txt на шаре) и
# спросит номер новой версии интерактивно.

param(
    [string]$Version,
    [ValidateSet("2023", "2026", "All")]
    [string]$Target = "All"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent

$allTargets = @(
    @{ Year = "2023"; BinRelease = "$root\bin\Release"; ServerRoot = "K:\02_BIM\DAN_Plugin\2023" }
    @{ Year = "2026"; BinRelease = "$root\bin2026\Release"; ServerRoot = "K:\02_BIM\DAN_Plugin\2026" }
)
$targetsToPublish = $allTargets | Where-Object { $Target -eq "All" -or $Target -eq $_.Year }

# Показываем, что сейчас лежит на шаре, ДО запроса новой версии — иначе легко случайно
# опубликовать номер меньше или равный уже выложенному.
foreach ($t in $targetsToPublish) {
    $versionFile = "$($t.ServerRoot)\version.txt"
    if (Test-Path $versionFile) {
        $currentVersion = (Get-Content -Path $versionFile -Raw).Trim()
        Write-Host "Текущая версия на шаре (Revit $($t.Year)): $currentVersion" -ForegroundColor Yellow
    }
    else {
        Write-Host "На шаре (Revit $($t.Year)) ещё ничего не публиковалось." -ForegroundColor Yellow
    }
}

if (-not $Version) {
    $Version = Read-Host "`nНомер версии для публикации (например 1.5.0)"
}
if (-not $Version) { Write-Error "Номер версии обязателен." }

function Publish-Plugin {
    param(
        [string]$RevitYear,
        [string]$BinRelease,
        [string]$ServerRoot,
        [string]$Version
    )

    if (-not (Test-Path $BinRelease)) {
        Write-Error "Не найдена папка сборки: $BinRelease. Сначала выполните build.ps1 -Target $RevitYear."
    }

    Write-Host "`nПубликация DAN_Plugin (Revit $RevitYear) версии $Version на $ServerRoot ..." -ForegroundColor Cyan

    if (-not (Test-Path $ServerRoot)) { New-Item -ItemType Directory -Path $ServerRoot -Force | Out-Null }

    # Раскладка файлов зеркалит [Files] из .iss-скрипта — то, что окажется в папке
    # установки на клиенте (LoaderApp.PluginDir), см. UpdateSync.CopyDirectory в Loader.cs.
    Copy-Item "$BinRelease\*.dll" $ServerRoot -Force

    $checklistSrc = "$BinRelease\Чеклист\checklist.html"
    if (Test-Path $checklistSrc) { Copy-Item $checklistSrc $ServerRoot -Force }

    $rebarZonesSrc = "$BinRelease\RebarZones\rebar_zones.html"
    if (Test-Path $rebarZonesSrc) {
        $rebarZonesDst = "$ServerRoot\RebarZones"
        if (-not (Test-Path $rebarZonesDst)) { New-Item -ItemType Directory -Path $rebarZonesDst -Force | Out-Null }
        Copy-Item $rebarZonesSrc $rebarZonesDst -Force
    }

    Set-Content -Path "$ServerRoot\version.txt" -Value $Version -NoNewline -Encoding utf8

    Write-Host "Готово. Версия $Version опубликована в $ServerRoot." -ForegroundColor Green
}

foreach ($t in $targetsToPublish) {
    Publish-Plugin -RevitYear $t.Year -BinRelease $t.BinRelease -ServerRoot $t.ServerRoot -Version $Version
}

Write-Host "`nПользователи получат её при следующем запуске Revit (если есть связь с сервером)." -ForegroundColor White
