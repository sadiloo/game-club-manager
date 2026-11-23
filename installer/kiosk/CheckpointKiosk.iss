; Checkpoint kiosk installer (B) — zero input pages; reboot via exit 3010 when needed (never forced).
#ifndef AppVersion
  #error AppVersion must be passed via /DAppVersion
#endif
#ifndef StagingKioskDir
  #define StagingKioskDir "..\..\artifacts\installer\stage-kiosk"
#endif
#ifndef StagingGuardDir
  #define StagingGuardDir "..\..\artifacts\installer\stage-guard"
#endif

#define AppName "چک‌پوینت کیوسک"
#define SetupBase "CheckpointKioskSetup-" + AppVersion

[Setup]
AppId={{1B3B2A4E-5A6D-4E5C-8F9A-2E1D3C4B5A6F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
DefaultDirName={pf}\Checkpoint\Kiosk
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableReadyPage=yes
PrivilegesRequired=admin
WizardStyle=modern
ShowLanguageDialog=no
OutputBaseFilename={#SetupBase}
OutputDir=Output
UninstallDisplayName={#AppName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "persian"; MessagesFile: "..\lang\Persian.isl"

[LangOptions]
RightToLeft=yes

[Files]
Source: "{#StagingKioskDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: KioskPayloadDirExists('{#StagingKioskDir}')
Source: "{#StagingGuardDir}\*"; DestDir: "{pf}\Checkpoint\Guard"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: KioskPayloadDirExists('{#StagingGuardDir}')
Source: "..\tools\kiosk-postinstall.ps1"; DestDir: "{pf}\Checkpoint\Guard\tools"; Flags: ignoreversion
Source: "..\tools\CheckpointInstall.Common.psm1"; DestDir: "{pf}\Checkpoint\Guard\tools"; Flags: ignoreversion
Source: "..\..\docs\install\kiosk-install.fa.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Dirs]
Name: "{commonappdata}\Checkpoint\kiosk"; Permissions: administrators-full

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{pf}\Checkpoint\Guard\tools\kiosk-postinstall.ps1"""; StatusMsg: "پیکربندی کاربر کیوسک و نگهبان…"; Flags: runhidden waituntilidle

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop CheckpointGuard"; Flags: runhidden
Filename: "sc.exe"; Parameters: "delete CheckpointGuard"; Flags: runhidden
Filename: "net.exe"; Parameters: "user CheckpointKiosk /delete"; Flags: runhidden

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{pf}\Checkpoint\Guard"

[Code]
function InitializeSetup: Boolean;
begin
  Result := True;
end;

function KioskPayloadDirExists(const Dir: string): Boolean;
begin
  Result := DirExists(Dir);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  NeedsRestart := True;
  Result := '';
end;