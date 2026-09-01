# Publish2026.ps1 — публикует собранный DAN_Plugin (Revit 2026) на сетевую шару обновлений
# K:\02_BIM\DAN_Plugin\2026\, откуда его подхватывают все рабочие станции при следующем
# запуске Revit (см. UpdateSync в Loader.cs). Запускать после Build2026.ps1 (или сборки
# вручную) — здесь используется уже собранный bin2026\Release.
#
# Использование:
#   .\Publish2026.ps1 -Version 1.5.0
# Без -Version скрипт спросит номер версии интерактивно.
param(
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root       = Split-Path $PSScriptRoot -Parent
$binRelease = "$root\bin2026\Release"
$serverRoot = "K:\02_BIM\DAN_Plugin\2026"

if (-not (Test-Path $binRelease)) {
    Write-Error "Не найдена папка сборки: $binRelease. Сначала выполните Build2026.ps1."
}

if (-not $Version) {
    $Version = Read-Host "Номер версии для публикации (например 1.5.0)"
}
if (-not $Version) { Write-Error "Номер версии обязателен." }

Write-Host "`nПубликация DAN_Plugin (Revit 2026) версии $Version на $serverRoot ..." -ForegroundColor Cyan

if (-not (Test-Path $serverRoot)) { New-Item -ItemType Directory -Path $serverRoot -Force | Out-Null }

# Раскладка файлов зеркалит [Files] из DAN_Plugin2026.iss — то, что окажется в папке
# установки на клиенте (LoaderApp.PluginDir), см. UpdateSync.CopyDirectory в Loader.cs.
Copy-Item "$binRelease\*.dll" $serverRoot -Force

$checklistSrc = "$binRelease\Чеклист\checklist.html"
if (Test-Path $checklistSrc) { Copy-Item $checklistSrc $serverRoot -Force }

$rebarZonesSrc = "$binRelease\RebarZones\rebar_zones.html"
if (Test-Path $rebarZonesSrc) {
    $rebarZonesDst = "$serverRoot\RebarZones"
    if (-not (Test-Path $rebarZonesDst)) { New-Item -ItemType Directory -Path $rebarZonesDst -Force | Out-Null }
    Copy-Item $rebarZonesSrc $rebarZonesDst -Force
}

Set-Content -Path "$serverRoot\version.txt" -Value $Version -NoNewline -Encoding utf8

Write-Host "Готово. Версия $Version опубликована в $serverRoot." -ForegroundColor Green
Write-Host "Пользователи получат её при следующем запуске Revit (если есть связь с сервером)." -ForegroundColor White
