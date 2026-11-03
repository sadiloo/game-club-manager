@echo off
setlocal EnableExtensions
REM Removes the per-user Winlogon shell and the lockdown values this installer wrote.
REM TLS 1.2 and .NET strong-crypto values are left in place. They are not kiosk-only.
REM Does not delete the Windows user unless /removeuser is passed.
REM Home editions have no Group Policy editor; removal is the same registry delete.

net session >nul 2>&1
if errorlevel 1 (
  echo Run uninstall-kiosk.cmd from an elevated command prompt.
  exit /b 1
)

set "KIOSKUSER=GameClubKiosk"
set "REMOVEUSER=0"
:parse
if "%~1"=="" goto parsed
if /I "%~1"=="/user" set "KIOSKUSER=%~2" & shift & shift & goto parse
if /I "%~1"=="/removeuser" set "REMOVEUSER=1" & shift & goto parse
echo Unknown argument %~1
exit /b 1
:parsed

sc stop GameClubKioskGuard >nul 2>&1
sc delete GameClubKioskGuard >nul 2>&1

for /f "delims=" %%S in ('powershell -NoProfile -Command "(New-Object System.Security.Principal.NTAccount('%KIOSKUSER%')).Translate([System.Security.Principal.SecurityIdentifier]).Value"') do set "SID=%%S"
set "PROFILE="
for /f "tokens=2,*" %%A in ('reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\%SID%" /v ProfileImagePath 2^>nul') do set "PROFILE=%%B"
if not "%PROFILE%"=="" (
  reg load "HKU\GameClubKiosk" "%PROFILE%\NTUSER.DAT" >nul
  if not errorlevel 1 (
    reg add "HKU\GameClubKiosk\Software\Microsoft\Windows NT\CurrentVersion\Winlogon" /v Shell /t REG_SZ /d "explorer.exe" /f >nul
    reg delete "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableTaskMgr /f >nul 2>&1
    reg delete "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableRegistryTools /f >nul 2>&1
    reg delete "HKU\GameClubKiosk\Software\Policies\Microsoft\Windows\System" /v DisableCMD /f >nul 2>&1
    reg delete "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableLockWorkstation /f >nul 2>&1
    reg delete "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableChangePassword /f >nul 2>&1
    reg delete "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoWinKeys /f >nul 2>&1
    reg delete "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoRun /f >nul 2>&1
    reg delete "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoControlPanel /f >nul 2>&1
    reg unload "HKU\GameClubKiosk" >nul
  ) else (
    echo Could not load the user hive. Log off %KIOSKUSER% and run uninstall again.
    exit /b 5
  )
)

reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v HideFastUserSwitching /f >nul 2>&1
reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoDriveTypeAutoRun /f >nul 2>&1
reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoAutorun /f >nul 2>&1
reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v SettingsPageVisibility /f >nul 2>&1

if "%REMOVEUSER%"=="1" net user "%KIOSKUSER%" /delete

echo Uninstalled. The kiosk user shell is explorer.exe again.
echo Shell Launcher and Assigned Access were not configured.
exit /b 0
