# New-Installer.ps1 — проставляет AppVersion в DAN_Plugin_Combined.iss и собирает
# DAN-брендированный установщик (BuildCombined.ps1). LD-вариант не трогает и не собирает —
# для него по-прежнему используется отдельно Installer\BuildCombinedLD.ps1.
#
# Использование:
#   .\New-Installer.ps1 -Version 2.4
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+(\.\d+){1,3}$')]
    [string]$Version
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$issFile = "$PSScriptRoot\DAN_Plugin_Combined.iss"

$content = [System.IO.File]::ReadAllText($issFile)
$pattern = '#define AppVersion "[^"]*"'
if (-not [System.Text.RegularExpressions.Regex]::IsMatch($content, $pattern)) {
    Write-Error "Не удалось найти строку '#define AppVersion' в $issFile."
}
$updated = [System.Text.RegularExpressions.Regex]::Replace($content, $pattern, "#define AppVersion `"$Version`"")
[System.IO.File]::WriteAllText($issFile, $updated, (New-Object System.Text.UTF8Encoding $false))
Write-Host "AppVersion в DAN_Plugin_Combined.iss -> $Version" -ForegroundColor Cyan

& "$PSScriptRoot\BuildCombined.ps1"
