; Тот же установщик, что DAN_Plugin_Combined.iss (тот же AppId/путь установки/manifest —
; вторая установка одного из двух вариантов на ту же машину заменяет первую), но ставит
; Loader.dll из сборки Release-LD (см. Loader.csproj/Loader2026.csproj) — единственное отличие
; в работе плагина: вкладка ленты Revit называется "LD", а не "DAN" (см. LD_BRAND в Loader.cs).
; DAN_Plugin.dll и все остальные файлы — те же самые, из обычной сборки Release.
#define AppName    "DAN Plugin"
#define AppVersion "2.2"
#define Publisher  "Daniil Levin"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
AppId={{6f3a2b7c-9d4e-4a8b-8b1a-2e5c7f9a1d3b}

; {app} явно не используется — у каждой версии Revit свой путь под {commonappdata},
; см. [Files]. DefaultDirName нужен только для служебных целей Inno Setup.
DefaultDirName={commonappdata}\Autodesk\Revit\Addins\DAN_Plugin
DisableDirPage=yes

OutputDir=Output
OutputBaseFilename=LD_Plugin_Setup_{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
; ── Revit 2023 — только если найдена папка Addins\2023 ──────────────────────
Source: "..\bin\Release\*.dll"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2023\DAN_Plugin"; \
    Flags: ignoreversion; Check: Has2023
Source: "..\bin\Release\Чеклист\checklist.html"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2023\DAN_Plugin"; \
    Flags: ignoreversion; Check: Has2023
Source: "..\bin\Release\RebarZones\rebar_zones.html"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2023\DAN_Plugin\RebarZones"; \
    Flags: ignoreversion; Check: Has2023
Source: "..\bin\Release\RebarZones\slab_mark_report.html"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2023\DAN_Plugin\RebarZones"; \
    Flags: ignoreversion; Check: Has2023
; LD-брендированный Loader.dll — идёт ПОСЛЕ общего *.dll выше и перетирает обычный Loader.dll.
Source: "..\bin\Release-LD\Loader.dll"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2023\DAN_Plugin"; \
    Flags: ignoreversion; Check: Has2023
Source: "Manifest.addin"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2023"; \
    Flags: ignoreversion; Check: Has2023

; ── Revit 2026 — только если найдена папка Addins\2026 ──────────────────────
Source: "..\bin2026\Release\*.dll"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026\DAN_Plugin"; \
    Flags: ignoreversion; Check: Has2026
Source: "..\bin2026\Release\Чеклист\checklist.html"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026\DAN_Plugin"; \
    Flags: ignoreversion; Check: Has2026
Source: "..\bin2026\Release\RebarZones\rebar_zones.html"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026\DAN_Plugin\RebarZones"; \
    Flags: ignoreversion; Check: Has2026
Source: "..\bin2026\Release\RebarZones\slab_mark_report.html"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026\DAN_Plugin\RebarZones"; \
    Flags: ignoreversion; Check: Has2026
; LD-брендированный Loader.dll — идёт ПОСЛЕ общего *.dll выше и перетирает обычный Loader.dll.
Source: "..\bin2026\Release-LD\Loader.dll"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026\DAN_Plugin"; \
    Flags: ignoreversion; Check: Has2026
Source: "Manifest2026.addin"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026"; \
    Flags: ignoreversion; Check: Has2026

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\Autodesk\Revit\Addins\2023\DAN_Plugin"
Type: filesandordirs; Name: "{commonappdata}\Autodesk\Revit\Addins\2026\DAN_Plugin"

[Code]
function Has2023: Boolean;
begin
  Result := DirExists(ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\2023'));
end;

function Has2026: Boolean;
begin
  Result := DirExists(ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\2026'));
end;

function InitializeSetup: Boolean;
begin
  if not Has2023 and not Has2026 then
  begin
    MsgBox(
      'Не найдена ни одна папка Addins Autodesk Revit (2023 или 2026):' + #13#10 +
      ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\2023') + #13#10 +
      ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\2026') + #13#10#13#10 +
      'Убедитесь, что установлен хотя бы один из этих Revit, и повторите установку.',
      mbError, MB_OK);
    Result := False;
  end
  else
    Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not Has2023 then
      Log('Revit 2023 не найден — установка для этой версии пропущена.');
    if not Has2026 then
      Log('Revit 2026 не найден — установка для этой версии пропущена.');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DeleteFile(ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\2023\Manifest.addin'));
    DeleteFile(ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\2026\Manifest2026.addin'));
  end;
end;
