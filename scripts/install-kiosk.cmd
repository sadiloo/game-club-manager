@echo off
setlocal EnableExtensions
REM Game Club kiosk installer for Windows 7 SP1, 8.1, 10, and 11 (Home and Pro).
REM Pilot PCs are Windows Home. No Group Policy editor and no edition-limited application control.
REM These registry values are the policies the Home editions honor themselves.
REM The shell is the per-user Winlogon Shell of the kiosk account. The machine shell is left alone.
REM Windows 8.0 is refused: .NET Framework 4.8 supports Windows 8.1, not the original Windows 8.
REM On Windows 7, install the SHA-2 update KB4474419 and the TLS update KB3140245 before a signed
REM installer or .NET Framework 4.8 will be trusted. This script then enables TLS 1.2 for .NET 4.8.
REM Steam, Epic, and Battle.net no longer support Windows 7 or 8.1. Local game executables still can.

net session >nul 2>&1
if errorlevel 1 (
  echo Run install-kiosk.cmd from an elevated command prompt.
  exit /b 1
)

set "KIOSKUSER=GameClubKiosk"
set "SHELLPATH="
set "USERPASSFILE="
set "ADMINPASSFILE="
set "GUARDPATH="

:parse
if "%~1"=="" goto parsed
if /I "%~1"=="/user" set "KIOSKUSER=%~2" & shift & shift & goto parse
if /I "%~1"=="/shell" set "SHELLPATH=%~2" & shift & shift & goto parse
if /I "%~1"=="/userpassfile" set "USERPASSFILE=%~2" & shift & shift & goto parse
if /I "%~1"=="/adminpassfile" set "ADMINPASSFILE=%~2" & shift & shift & goto parse
if /I "%~1"=="/guard" set "GUARDPATH=%~2" & shift & shift & goto parse
echo Unknown argument %~1
exit /b 1

:parsed
if "%SHELLPATH%"=="" (
  echo Usage: install-kiosk.cmd /shell "C:\Program Files\GameClub\GameClub.Kiosk.exe" /guard "...\GameClub.Kiosk.Guard.exe" /userpassfile pass.txt /adminpassfile admin.txt
  exit /b 1
)

for /f "tokens=2,*" %%A in ('reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v ProductName') do set "PRODUCT=%%B"
echo Detected %PRODUCT%
echo %PRODUCT% | findstr /I /C:"Windows 8.1" >nul
if not errorlevel 1 goto os_ok
echo %PRODUCT% | findstr /I /C:"Windows 8" >nul
if not errorlevel 1 (
  echo Windows 8 must be upgraded to Windows 8.1. .NET Framework 4.8 does not install on Windows 8.
  exit /b 2
)
:os_ok

set "RELEASE=0"
for /f "tokens=3" %%A in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2^>nul') do set "RELEASE=%%A"
set /a RELNUM=%RELEASE% 2>nul
if %RELNUM% LSS 528040 (
  echo .NET Framework 4.8 or newer is required. On Windows 7, install KB4474419 ^(SHA-2^) and KB3140245 ^(TLS 1.2^) first.
  exit /b 3
)
echo %PRODUCT% | findstr /I /C:"Windows 7" >nul
if not errorlevel 1 (
  echo Windows 7: confirming TLS 1.2 registry. A signed installer also needs the SHA-2 code-signing update KB4474419.
)

net user "%KIOSKUSER%" >nul 2>&1
if errorlevel 1 (
  if "%USERPASSFILE%"=="" (
    echo The kiosk user does not exist. Pass /userpassfile with one line, the password.
    exit /b 4
  )
  call :CreateKioskUser
  if errorlevel 1 exit /b 4
  net localgroup Users "%KIOSKUSER%" /add >nul 2>&1
)
wmic useraccount where "name='%KIOSKUSER%'" set PasswordExpires=FALSE >nul 2>&1

for /f "delims=" %%S in ('powershell -NoProfile -Command "(New-Object System.Security.Principal.NTAccount('%KIOSKUSER%')).Translate([System.Security.Principal.SecurityIdentifier]).Value"') do set "SID=%%S"
if "%SID%"=="" (
  echo Could not resolve the kiosk user SID.
  exit /b 5
)

set "PROFILE="
for /f "tokens=2,*" %%A in ('reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\%SID%" /v ProfileImagePath 2^>nul') do set "PROFILE=%%B"
if "%PROFILE%"=="" (
  echo Creating the user profile. Log off the kiosk user before continuing.
  powershell -NoProfile -Command "$b = New-Object System.Text.StringBuilder 260; Add-Type -Namespace GameClub -Name Native -MemberDefinition '[System.Runtime.InteropServices.DllImport(\"userenv.dll\", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] public static extern int CreateProfile(string sid, string userName, System.Text.StringBuilder path, int size);'; $code = [GameClub.Native]::CreateProfile('%SID%', '%KIOSKUSER%', $b, 260); Write-Output $b.ToString(); exit 0"
  for /f "tokens=2,*" %%A in ('reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\%SID%" /v ProfileImagePath 2^>nul') do set "PROFILE=%%B"
)
if "%PROFILE%"=="" (
  echo The profile was not created. Sign in once as %KIOSKUSER% and run this installer again while that user is logged off.
  exit /b 5
)

reg load "HKU\GameClubKiosk" "%PROFILE%\NTUSER.DAT" >nul
if errorlevel 1 (
  echo Could not load NTUSER.DAT. The kiosk user must be logged off.
  exit /b 5
)

reg add "HKU\GameClubKiosk\Software\Microsoft\Windows NT\CurrentVersion\Winlogon" /v Shell /t REG_SZ /d "%SHELLPATH%" /f >nul
reg add "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableTaskMgr /t REG_DWORD /d 1 /f >nul
reg add "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableRegistryTools /t REG_DWORD /d 1 /f >nul
reg add "HKU\GameClubKiosk\Software\Policies\Microsoft\Windows\System" /v DisableCMD /t REG_DWORD /d 1 /f >nul
reg add "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableLockWorkstation /t REG_DWORD /d 1 /f >nul
reg add "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableChangePassword /t REG_DWORD /d 1 /f >nul
reg add "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoWinKeys /t REG_DWORD /d 1 /f >nul
reg add "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoRun /t REG_DWORD /d 1 /f >nul
reg add "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoControlPanel /t REG_DWORD /d 1 /f >nul
reg unload "HKU\GameClubKiosk" >nul

reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v HideFastUserSwitching /t REG_DWORD /d 1 /f >nul
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoDriveTypeAutoRun /t REG_DWORD /d 255 /f >nul
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoAutorun /t REG_DWORD /d 1 /f >nul
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v SettingsPageVisibility /t REG_SZ /d "hide:allpages" /f >nul

REM TLS 1.2. Required on Windows 7. Harmless where the OS already defaults to it. 2560 is 0xA00.
reg add "HKLM\SOFTWARE\Microsoft\.NETFramework\v4.0.30319" /v SchUseStrongCrypto /t REG_DWORD /d 1 /f >nul
reg add "HKLM\SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v4.0.30319" /v SchUseStrongCrypto /t REG_DWORD /d 1 /f >nul
reg add "HKLM\SOFTWARE\Microsoft\.NETFramework\v4.0.30319" /v SystemDefaultTlsVersions /t REG_DWORD /d 1 /f >nul
reg add "HKLM\SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v4.0.30319" /v SystemDefaultTlsVersions /t REG_DWORD /d 1 /f >nul
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp" /v DefaultSecureProtocols /t REG_DWORD /d 2560 /f >nul
reg add "HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp" /v DefaultSecureProtocols /t REG_DWORD /d 2560 /f >nul
reg add "HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client" /v Enabled /t REG_DWORD /d 1 /f >nul
reg add "HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client" /v DisabledByDefault /t REG_DWORD /d 0 /f >nul

if not exist "%ProgramData%\GameClub" mkdir "%ProgramData%\GameClub"
>"%ProgramData%\GameClub\shell.path" echo %SHELLPATH%

if not "%GUARDPATH%"=="" (
  sc stop GameClubKioskGuard >nul 2>&1
  sc delete GameClubKioskGuard >nul 2>&1
  sc create GameClubKioskGuard binPath= "\"%GUARDPATH%\"" start= auto
  sc description GameClubKioskGuard "Restarts the kiosk shell and stops escape processes."
  if not "%ADMINPASSFILE%"=="" (
    if not exist "%ProgramData%\GameClub" mkdir "%ProgramData%\GameClub"
    "%GUARDPATH%" hash-admin-file "%ADMINPASSFILE%" "%ProgramData%\GameClub\admin-gate.json"
  )
  sc start GameClubKioskGuard >nul 2>&1
)

echo Installed. Kiosk user %KIOSKUSER% starts %SHELLPATH% instead of explorer.exe.
echo Admin escape is Ctrl+Shift+F12 inside the shell, then the admin password.
echo Shell Launcher and Assigned Access are not used. They are not available on these editions.
exit /b 0

:CreateKioskUser
set /p KIOSKPASS=<"%USERPASSFILE%"
net user "%KIOSKUSER%" "%KIOSKPASS%" /add /passwordchg:no /expires:never /fullname:"Game Club Kiosk"
exit /b %ERRORLEVEL%
