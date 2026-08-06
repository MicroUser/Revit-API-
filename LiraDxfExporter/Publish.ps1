# Собирает LiraDxfExporter в один самодостаточный LiraDxfExporter.exe (весь .NET-рантайм и
# зависимость DxfCleaner упакованы внутрь) — раздавать пользователям можно одним этим файлом,
# без установки .NET и без соседних .dll.
dotnet publish "$PSScriptRoot\LiraDxfExporter.csproj" -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o "$PSScriptRoot\publish"

Write-Host "Done: $PSScriptRoot\publish\LiraDxfExporter.exe"
