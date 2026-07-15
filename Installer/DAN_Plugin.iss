#define AppName    "DAN Plugin"
#define AppVersion "1.2"
#define Publisher  "Daniil Levin"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
AppId={{d15d37d2-4b1e-495e-8d48-024cd9e9e134}

DefaultDirName={commonappdata}\Autodesk\Revit\Addins\2023\DAN_Plugin
DisableDirPage=yes

OutputDir=Output
OutputBaseFilename=DAN_Plugin_Setup_{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
; DLL-библиотеки плагина → DAN_Plugin\
Source: "..\bin\Release\*.dll";                     DestDir: "{app}"; Flags: ignoreversion
; HTML чеклиста
Source: "..\bin\Release\Чеклист\checklist.html";    DestDir: "{app}"; Flags: ignoreversion
; Манифест → корень Addins 2023\
Source: "Manifest.addin"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2023"; Flags: ignoreversion

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function InitializeSetup: Boolean;
var
  AddinsPath: String;
begin
  AddinsPath := ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\2023');
  if not DirExists(AddinsPath) then
  begin
    MsgBox(
      'Папка Addins Autodesk Revit 2023 не найдена:' + #13#10 +
      AddinsPath + #13#10#13#10 +
      'Убедитесь, что Revit 2023 установлен, и повторите установку.',
      mbError, MB_OK);
    Result := False;
  end
  else
    Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    DeleteFile(ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\2023\Manifest.addin'));
end;
