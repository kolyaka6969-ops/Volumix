; Установщик Volumix (Inno Setup 6). Собирается скриптом build.ps1 в корне.

#define AppName "Volumix"
#ifndef AppVersion
  #define AppVersion "1.6.4"
#endif
#ifndef SetupName
  #define SetupName "Volumix-Setup-" + AppVersion
#endif
#define AppExe "Volumix.exe"
#define PublishDir "..\publish"

[Setup]
AppId={{8F3C2A51-7B4E-4C1D-9E2A-5D6F7A8B9C10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
; Установка для текущего пользователя — без запроса прав администратора
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\dist
OutputBaseFilename={#SetupName}
SetupIconFile=..\src\Volumix\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.AutostartTask=Запускать Volumix вместе с Windows
english.AutostartTask=Start Volumix with Windows
russian.ExtraGroup=Дополнительно:
english.ExtraGroup=Additional options:
russian.RemoveSettings=Удалить также настройки Volumix (горячие клавиши, тема)?
english.RemoveSettings=Also remove Volumix settings (hotkeys, theme)?

[Tasks]
Name: "autostart"; Description: "{cm:AutostartTask}"; GroupDescription: "{cm:ExtraGroup}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Голосовые команды убраны — модель распознавания речи (~90 МБ) от прежних версий больше не нужна
Type: filesandordirs; Name: "{app}\models"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Тот же ключ, что переключается в настройках программы
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Volumix"; ValueData: """{app}\{#AppExe}"" --tray"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure KillApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  KillApp();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Автозапуск мог быть включён из самой программы, а не установщиком
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Volumix');
    if (not UninstallSilent) and
       (MsgBox(CustomMessage('RemoveSettings'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(ExpandConstant('{userappdata}\Volumix'), True, True, True);
  end;
end;
