#define MyAppName "GitVisualizer"
#define MyAppVersion "1.3.2"
#define MyAppPublisher "GitVisualizer"
#define MyAppExeName "GitVisualizer.exe"
#define MyAppUserModelId "GitVisualizer.App.1.3.2"

[Setup]
AppId={{A7A2199C-88BE-46B2-A11F-1F635E838697}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (C) 2026 GitVisualizer
VersionInfoVersion=1.3.2.0
VersionInfoProductVersion=1.3.2
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=GitVisualizer v1.3.2 安装程序
DefaultDirName={autopf}\GitVisualizer
DefaultGroupName=GitVisualizer
AllowNoIcons=yes
DisableDirPage=no
DisableProgramGroupPage=no
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern dynamic
SetupIconFile=GitVisualizer.ico
UninstallDisplayName=GitVisualizer v1.3.2
UninstallDisplayIcon={app}\uninstall.exe
UninstallFilesDir={app}\.uninstall
OutputDir=output
OutputBaseFilename=GitVisualizer-v1.3.2-Setup
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
InfoBeforeFile=Installer-Info.txt
InfoAfterFile=Installer-Complete.txt

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式（默认）"; GroupDescription: "附加快捷方式："
Name: "taskbarguide"; Description: "安装完成后启动 GitVisualizer 并显示任务栏固定指引"; GroupDescription: "任务栏："; Flags: unchecked

[Files]
Source: "..\artifacts\publish\win-x64\GitVisualizer.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "uninstall.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\RELEASE-NOTES.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Dirs]
Name: "{app}\.uninstall"; Attribs: hidden

[Icons]
Name: "{group}\GitVisualizer"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; AppUserModelID: "{#MyAppUserModelId}"
Name: "{group}\卸载 GitVisualizer"; Filename: "{app}\uninstall.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\GitVisualizer"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; AppUserModelID: "{#MyAppUserModelId}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 GitVisualizer"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent; Check: not WizardIsTaskSelected('taskbarguide')

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Started: Boolean;
begin
  if (CurStep = ssPostInstall) and
     (not WizardSilent) and
     WizardIsTaskSelected('taskbarguide') then
  begin
    Started := Exec(
      ExpandConstant('{app}\{#MyAppExeName}'),
      '',
      ExpandConstant('{app}'),
      SW_SHOWNORMAL,
      ewNoWait,
      ResultCode);

    if Started then
    begin
      Sleep(1200);
      MsgBox(
        'Windows 要求由您本人确认任务栏固定。' + #13#10 + #13#10 +
        'GitVisualizer 已启动。请右键单击任务栏上的 GitVisualizer 图标，' +
        '然后选择“固定到任务栏”。',
        mbInformation,
        MB_OK);
    end
    else
    begin
      MsgBox(
        '已完成安装，但未能自动启动程序。' + #13#10 +
        '您可以从开始菜单启动 GitVisualizer，再右键单击其任务栏图标进行固定。',
        mbInformation,
        MB_OK);
    end;
  end;
end;
