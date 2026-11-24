; Checkpoint server installer (B) — full payload staged by ci-installer.ps1
#ifndef AppVersion
  #error AppVersion must be passed via /DAppVersion
#endif
#ifndef StagingServerDir
  #define StagingServerDir "..\..\artifacts\installer\stage-server"
#endif
#ifndef StagingPgDir
  #define StagingPgDir "..\..\artifacts\installer\stage-pg-trimmed"
#endif
#ifndef StagingWebView2
  #define StagingWebView2 "..\..\artifacts\installer\stage-webview2\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
#endif

#define AppName "چک‌پوینت سرور"
#define SetupBase "CheckpointServerSetup-" + AppVersion

[Setup]
AppId={{8E5E6B2E-6B6D-4F5B-9D9A-4F4E7F8F6B6D}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
DefaultDirName={pf}\Checkpoint\Server
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
WizardStyle=modern
WizardSizePercent=120
ShowLanguageDialog=no
LanguageDetectionMethod=none
OutputBaseFilename={#SetupBase}
OutputDir=Output
UninstallDisplayName={#AppName}
SetupLogging=yes
CloseApplications=force
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesAssociations=no

[Languages]
Name: "persian"; MessagesFile: "..\lang\Persian.isl"

[CustomMessages]
persian.inst_server_finish_ipLabel=TODO_UIUX inst.server.finish.ipLabel
persian.inst_server_finish_dhcpNote=TODO_UIUX inst.server.finish.dhcpNote

[LangOptions]
RightToLeft=yes

[Files]
Source: "{#StagingServerDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: PayloadDirExists('{#StagingServerDir}')
Source: "{#StagingPgDir}\*"; DestDir: "{pf}\Checkpoint\PostgreSQL"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: PayloadDirExists('{#StagingPgDir}')
Source: "{#StagingWebView2}"; DestDir: "{app}\_thirdparty"; Flags: ignoreversion; Check: PayloadFileExists('{#StagingWebView2}')
Source: "..\tools\*.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\tools\CheckpointInstall.Common.psm1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\..\docs\install\server-install.fa.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Dirs]
Name: "{commonappdata}\Checkpoint"; Permissions: admins-full
Name: "{commonappdata}\Checkpoint\config"; Permissions: admins-full
Name: "{commonappdata}\Checkpoint\pgdata"; Permissions: admins-full
Name: "{commonappdata}\Checkpoint\backups"; Permissions: admins-full
Name: "{commonappdata}\Checkpoint\logs"; Permissions: admins-full

[Icons]
Name: "{userdesktop}\چک‌پوینت — پیشخوان"; Filename: "{app}\GameClub.Shell.exe"; Tasks: desktopicon; Check: FileExists(ExpandConstant('{app}\GameClub.Shell.exe'))
Name: "{group}\چک‌پوینت — پیشخوان"; Filename: "{app}\GameClub.Shell.exe"; Check: FileExists(ExpandConstant('{app}\GameClub.Shell.exe'))
Name: "{group}\راهنمای نصب"; Filename: "{app}\docs\server-install.fa.md"

[Tasks]
Name: "desktopicon"; Description: "میان‌بر روی میزکار"; Flags: unchecked

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\server-postinstall.ps1"" -InstallPhase webview2"; StatusMsg: "در حال نصب WebView2…"; Flags: runhidden; Check: NeedWebView2Install
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\server-postinstall.ps1"" -InstallPhase database"; StatusMsg: "در حال آماده کردن پایگاه داده…"; Flags: runhidden; Check: IsFirstInstall
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\server-postinstall.ps1"" -InstallPhase accounts -AccountsFile ""{code:GetAccountsFile}"""; StatusMsg: "در حال آماده کردن حساب‌ها…"; Flags: runhidden; Check: IsFirstInstall
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\server-postinstall.ps1"" -InstallPhase services"; StatusMsg: "در حال نصب سرویس‌های چک‌پوینت…"; Flags: runhidden; Check: IsFirstInstall
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\server-postinstall.ps1"" -InstallPhase firewall"; StatusMsg: "در حال تنظیم فایروال…"; Flags: runhidden; Check: IsFirstInstall
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\server-postinstall.ps1"" -InstallPhase backup -AccountsFile ""{code:GetAccountsFile}"""; StatusMsg: "در حال تنظیم پشتیبان روزانه…"; Flags: runhidden; Check: IsFirstInstall
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\server-postinstall.ps1"" -InstallPhase start"; StatusMsg: "در حال روشن کردن سرور…"; Flags: runhidden; Check: IsFirstInstall
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\server-postinstall.ps1"" -InstallMode upgrade -InstallPhase upgrade-pins -DefaultPinsFile ""{code:GetDefaultPinsFile}"""; StatusMsg: "در حال آماده‌سازی ارتقا…"; Flags: runhidden; Check: IsUpgradeInstallCheck and UpgradeDefaultPinsReady and UpgradeHasDefaultPinsFile
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\server-postinstall.ps1"" -InstallMode upgrade -InstallPhase upgrade-stop"; StatusMsg: "در حال توقف سرور…"; Flags: runhidden; Check: IsUpgradeInstallCheck and UpgradeDefaultPinsReady
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\server-postinstall.ps1"" -InstallMode upgrade -InstallPhase upgrade-backup"; StatusMsg: "در حال گرفتن پشتیبان پیش از ارتقا…"; Flags: runhidden; Check: IsUpgradeInstallCheck and UpgradeDefaultPinsReady
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\server-postinstall.ps1"" -InstallMode upgrade -InstallPhase upgrade-start -AccountsFile ""{code:GetAccountsFile}"""; StatusMsg: "در حال روشن کردن سرور…"; Flags: runhidden; Check: IsUpgradeInstallCheck and UpgradeDefaultPinsReady
Filename: "{app}\docs\server-install.fa.md"; Description: "مشاهدهٔ راهنمای نصب"; Flags: postinstall shellexec skipifsilent unchecked; Check: FileExists(ExpandConstant('{app}\docs\server-install.fa.md'))

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop CheckpointServer"; Flags: runhidden
Filename: "sc.exe"; Parameters: "delete CheckpointServer"; Flags: runhidden
Filename: "sc.exe"; Parameters: "stop CheckpointPostgres"; Flags: runhidden
Filename: "sc.exe"; Parameters: "delete CheckpointPostgres"; Flags: runhidden
Filename: "schtasks.exe"; Parameters: "/Delete /TN CheckpointDailyBackup /F"; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Checkpoint Server API"""; Flags: runhidden

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
#include "ServerFinish.inc.iss"
#include "ServerWizard.inc.iss"

function PayloadDirExists(const Dir: string): Boolean;
begin
  Result := DirExists(Dir);
end;

function PayloadFileExists(const Path: string): Boolean;
begin
  Result := FileExists(Path);
end;

function GetAccountsFile(Param: String): String;
begin
  if ExpandConstant('{param:ACCOUNTS|}') <> '' then
    Result := ExpandConstant('{param:ACCOUNTS|}')
  else
    Result := AccountsJsonPath;
end;

function InitializeSetup: Boolean;
var Installed, NewVer: String; Cmp: Integer;
begin
  CaptureFirstInstallState;
  Installed := GetInstalledDisplayVersion;
  NewVer := '{#AppVersion}';
  if Installed <> '' then
  begin
    Cmp := CompareVersionParts(Installed, NewVer);
    if Cmp > 0 then
    begin
      MsgBox(Format('نسخهٔ نصب‌شده (%s) از این نصاب (%s) تازه‌تر است. برای برگشت به نسخهٔ قبلی، اول آن را حذف کنید.', [Installed, NewVer]), mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
  Result := True;
end;