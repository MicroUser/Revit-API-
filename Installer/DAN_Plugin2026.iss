#define AppName    "DAN Plugin (Revit 2026)"
#define AppVersion "1.2"
#define Publisher  "Daniil Levin"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
AppId={{ee0f1ec6-bd2e-4e6c-85e4-7c092c87e3fb}

DefaultDirName={commonappdata}\Autodesk\Revit\Addins\2026\DAN_Plugin
DisableDirPage=yes

OutputDir=Output
OutputBaseFilename=DAN_Plugin2026_Setup_{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
; DLL-библиотеки плагина (DAN_Plugin.dll, Loader.dll и NuGet-зависимости .NET 8) → DAN_Plugin\
Source: "..\bin2026\Release\*.dll";                     DestDir: "{app}"; Flags: ignoreversion
; HTML чеклиста — WebAssets.ResolveFolder() ищет его прямо рядом с DLL (или в подпапке
; "web"), НЕ в "Чеклист" — хотя в выходной папке сборки он лежит в Чеклист\, класть нужно
; прямо в {app} (как и в DAN_Plugin.iss для 2023).
Source: "..\bin2026\Release\Чеклист\checklist.html";    DestDir: "{app}"; Flags: ignoreversion
; HTML редактора зон допармирования
Source: "..\bin2026\Release\RebarZones\rebar_zones.html"; DestDir: "{app}\RebarZones"; Flags: ignoreversion
; Манифест → корень Addins 2026\
Source: "Manifest2026.addin"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026"; Flags: ignoreversion

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function InitializeSetup: Boolean;
var
  AddinsPath: String;
begin
  AddinsPath := ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\2026');
  if not DirExists(AddinsPath) then
  begin
    MsgBox(
      'Папка Addins Autodesk Revit 2026 не найдена:' + #13#10 +
      AddinsPath + #13#10#13#10 +
      'Убедитесь, что Revit 2026 установлен, и повторите установку.',
      mbError, MB_OK);
    Result := False;
  end
  else
    Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    DeleteFile(ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\2026\Manifest2026.addin'));
end;
