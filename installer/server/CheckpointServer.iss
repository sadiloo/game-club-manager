; Checkpoint server installer skeleton (B). Extended on Windows CI.
#define AppName "چک‌پوینت سرور"
#define AppVersion "0.0.0-b"

[Setup]
AppId={{8E5E6B2E-6B6D-4F5B-9D9A-4F4E7F8F6B6D}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={pf}\Checkpoint\Server
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
WizardStyle=modern
WizardSizePercent=120
ShowLanguageDialog=no
LanguageDetectionMethod=none

[Languages]
Name: "persian"; MessagesFile: "..\lang\Persian.isl"

[Files]
; Publish output staged by ci-installer.ps1 into {#SourcePath}

[Run]
Filename: "{app}\GameClub.Server.exe"; Description: "شروع سرور"; Flags: postinstall nowait skipifsilent

[Code]
function InitializeSetup: Boolean;
begin
  Result := True;
end;