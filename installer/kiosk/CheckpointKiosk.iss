; Checkpoint kiosk installer skeleton (B). Zero wizard pages except finish countdown.
#define AppName "چک‌پوینت کیوسک"
#define AppVersion "0.0.0-b"

[Setup]
AppId={{1B3B2A4E-5A6D-4E5C-8F9A-2E1D3C4B5A6F}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={pf}\Checkpoint\Kiosk
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableReadyPage=yes
PrivilegesRequired=admin
WizardStyle=modern
ShowLanguageDialog=no

[Languages]
Name: "persian"; MessagesFile: "..\lang\Persian.isl"

[Files]
; Kiosk publish output staged by ci-installer.ps1

[Run]
Filename: "shutdown.exe"; Parameters: "/r /t 10"; Description: "راه‌اندازی دوباره"; Flags: postinstall shellexec