; Установщик плагина AutoCAD+Civil (BLOCKTABLE / MAFCONVERT) — ставится в bundle-папку
; ApplicationPlugins ТЕКУЩЕГО пользователя (не общесистемную ProgramData): вживую проверено
; (см. переписку по разработке), что именно {userappdata}\Autodesk\ApplicationPlugins
; реально сканируется автозагрузчиком AutoCAD 2025 у этого пользователя — поэтому
; PrivilegesRequired=lowest, права администратора не нужны.
#define AppName    "AutoCAD+Civil"
#define AppVersion "1.0.0"
#define Publisher  "Daniil Levin"
#define BundleName "AutoCAD+Civil.bundle"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
AppId={{2A7AF252-7905-47C7-940D-BE6558F48570}

DefaultDirName={userappdata}\Autodesk\ApplicationPlugins\{#BundleName}
DisableDirPage=yes
DisableProgramGroupPage=yes

OutputDir=Output
OutputBaseFilename=AutoCAD_Civil_Setup_{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=AutoCADCivil.ico

PrivilegesRequired=lowest

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "..\AutoCAD+Civil\PackageContents.xml"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\AutoCAD+Civil\icon.ico"; DestDir: "{app}\Contents"; Flags: ignoreversion
; AutoCAD 2025 (.NET 8) — всегда есть, собирается на этой машине.
Source: "..\AutoCAD+Civil\bin\Release\net8.0-windows\AutoCAD+Civil.dll"; DestDir: "{app}\Contents\2025"; Flags: ignoreversion
; AutoCAD 2020 (.NET Framework 4.8) — необязательная сборка: собирается, только если рядом
; с проектом лежат версионно-специфичные ссылки AutoCAD 2020 (см. AutoCAD+Civil2020.csproj) и
; BuildAutoCADCivil.ps1 её действительно собрал. "Flags: skipifsourcedoesntexist" — не ломает
; установку, если 2020-сборки нет (PackageContents.xml всё равно ссылается на оба
; ComponentEntry, но AutoCAD 2020, если он не установлен у пользователя, эту ветку просто
; не тронет).
Source: "..\AutoCAD+Civil\bin2020\Release\AutoCAD+Civil.dll"; DestDir: "{app}\Contents\2020"; Flags: ignoreversion skipifsourcedoesntexist

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function InitializeSetup: Boolean;
begin
  if not DirExists(ExpandConstant('{userappdata}\Autodesk')) then
  begin
    MsgBox(
      'Не найдена папка Autodesk в профиле пользователя:' + #13#10 +
      ExpandConstant('{userappdata}\Autodesk') + #13#10#13#10 +
      'Убедитесь, что AutoCAD хотя бы раз был запущен под этим пользователем, и повторите установку.',
      mbError, MB_OK);
    Result := False;
  end
  else
    Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    MsgBox('Готово. Если AutoCAD сейчас открыт — перезапустите его: бандлы ' +
           'сканируются только при старте программы, на лету новые команды не подхватятся.',
           mbInformation, MB_OK);
end;
