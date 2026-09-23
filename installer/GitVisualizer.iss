; Copyright 2026 赵泽璇
; SPDX-License-Identifier: Apache-2.0

#ifndef AppName
  #define AppName "GitVisualizer"
#endif
#define AppVersion "1.3.3"
#ifndef LauncherSource
  #define LauncherSource "bin\卸载 GitVisualizer.exe"
#endif

[Setup]
#ifdef TestBuild
AppId=GitVisualizer.Uninstall.QA
#else
AppId={{A7A2199C-88BE-46B2-A11F-1F635E838697}
#endif
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} v{#AppVersion}
AppPublisher=GitVisualizer
VersionInfoVersion=1.3.3.0
VersionInfoDescription=GitVisualizer 安装程序
DefaultDirName={autopf}\GitVisualizer
DefaultGroupName={#AppName}
DisableDirPage=no
DisableProgramGroupPage=yes
DisableWelcomePage=no
LicenseFile=..\LICENSE
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
WizardSizePercent=115
SetupIconFile=..\src\GitVisualizer.App\Assets\GitVisualizer.ico
WizardImageFile=assets\wizard.bmp
WizardSmallImageFile=assets\header.bmp
UninstallDisplayName=GitVisualizer v{#AppVersion}
UninstallDisplayIcon={app}\GitVisualizer.exe
UninstallFilesDir={app}\.uninstall
OutputDir=..\release
OutputBaseFilename=GitVisualizer-v{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter=GitVisualizer.exe
RestartApplications=no
SetupLogging=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[LangOptions]
DialogFontName=Microsoft YaHei UI
DialogFontSize=9
WelcomeFontName=Microsoft YaHei UI
WelcomeFontSize=14

[Messages]
WelcomeLabel1=欢迎安装 GitVisualizer
WelcomeLabel2=轻松查看提交历史、管理分支与工作区。%n%n安装向导将帮助您选择安装位置和快捷方式。%n%n本安装包包含运行所需组件，无需另行安装 .NET。
ConfirmUninstall=是否完全卸载 %1？%n%n将永久删除当前用户的应用设置、操作日志、草稿、恢复点、运行缓存和本程序保存的凭据。%n%n不会删除您的 Git 仓库及其他软件保存的凭据。此操作不可恢复。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷访问："

[Files]
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\NOTICE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\licenses\*"; DestDir: "{app}\docs\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\win-x64\GitVisualizer.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "使用说明.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#LauncherSource}"; DestDir: "{app}"; DestName: "卸载 GitVisualizer.exe"; Flags: ignoreversion

[Dirs]
Name: "{app}\.uninstall"; Attribs: hidden

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\GitVisualizer.exe"; WorkingDir: "{app}"; IconFilename: "{app}\GitVisualizer.exe"
Name: "{group}\卸载 GitVisualizer"; Filename: "{app}\卸载 GitVisualizer.exe"; IconFilename: "{app}\卸载 GitVisualizer.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\GitVisualizer.exe"; WorkingDir: "{app}"; IconFilename: "{app}\GitVisualizer.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GitVisualizer.exe"; Description: "安装完成后运行 GitVisualizer"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
procedure BrowseInstallDirectory(Sender: TObject);
var
  Directory, ParentDirectory: String;
begin
  Directory := WizardForm.DirEdit.Text;
  { Start at the nearest existing parent when the app folder is not created yet. }
  while not DirExists(Directory) do
  begin
    ParentDirectory := ExtractFileDir(Directory);
    if (ParentDirectory = Directory) or (ParentDirectory = '') then
      Break;
    Directory := ParentDirectory;
  end;
  if BrowseForFolder('选择安装位置（将在所选位置下安装 GitVisualizer）', Directory, True) then
  begin
    { Keep application files in their own folder; avoid a duplicate suffix. }
    if CompareText(ExtractFileName(RemoveBackslashUnlessRoot(Directory)), 'GitVisualizer') <> 0 then
      Directory := AddBackslash(Directory) + 'GitVisualizer';
    WizardForm.DirEdit.Text := Directory;
  end;
end;

procedure InitializeWizard();
begin
  WizardForm.DirBrowseButton.OnClick := @BrowseInstallDirectory;
end;
function RunCleanupHelper(const Mode: String): Boolean;
var ExitCode: Integer;
begin
  Result := Exec(ExpandConstant('{app}\卸载 GitVisualizer.exe'), Mode,
    ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode);
  if Result then Result := ExitCode = 0;
end;

function InitializeUninstall(): Boolean;
begin
  Result := RunCleanupHelper('--check');
  if not Result then
    SuppressibleMsgBox('无法继续卸载。请先关闭所有 GitVisualizer 窗口；详情见安装目录 .uninstall\cleanup-error.txt。', mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    if not RunCleanupHelper('--cleanup') then
      RaiseException('应用数据未能完全清理，卸载已中止。请关闭占用数据的程序后重试；详情见 .uninstall\cleanup-error.txt。');
end;
