@echo off
setlocal EnableExtensions
REM Removes the per-user Winlogon shell and the lockdown values this installer wrote.
REM Every value this installer changed is imported from the backup, or deleted when no backup exists.
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

for /f "delims=" %%S in ('powershell -NoProfile -Command "(New-Object System.Security.Principal.NTAccount('%KIOSKUSER%')).Translate([System.Security.Principal.SecurityIdentifier]).Value"') do set "SID=%%S"
set "PROFILE="
for /f "tokens=2,*" %%A in ('reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\%SID%" /v ProfileImagePath 2^>nul') do set "PROFILE=%%B"
if not "%PROFILE%"=="" (
  reg unload "HKU\GameClubKiosk" >nul 2>&1
  reg load "HKU\GameClubKiosk" "%PROFILE%\NTUSER.DAT" >nul
  if errorlevel 1 (
    echo Could not load the user hive. Nothing was changed. Log off %KIOSKUSER% and run uninstall again.
    exit /b 5
  )
  reg unload "HKU\GameClubKiosk" >nul
)

sc stop GameClubKioskGuard >nul 2>&1
sc delete GameClubKioskGuard >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0lsa-secret.ps1" -SecretName DefaultPassword -Clear >nul 2>&1

if exist "%ProgramData%\GameClub\backup\hklm-policies.reg" (reg import "%ProgramData%\GameClub\backup\hklm-policies.reg") else (
  reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v HideFastUserSwitching /f >nul 2>&1
  reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v ConsentPromptBehaviorUser /f >nul 2>&1
  reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoDriveTypeAutoRun /f >nul 2>&1
  reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoAutorun /f >nul 2>&1
)
if exist "%ProgramData%\GameClub\backup\hklm-dotnet.reg" (reg import "%ProgramData%\GameClub\backup\hklm-dotnet.reg") else (
  reg delete "HKLM\SOFTWARE\Microsoft\.NETFramework\v4.0.30319" /v SchUseStrongCrypto /f >nul 2>&1
  reg delete "HKLM\SOFTWARE\Microsoft\.NETFramework\v4.0.30319" /v SystemDefaultTlsVersions /f >nul 2>&1
)
if exist "%ProgramData%\GameClub\backup\hklm-dotnet-wow.reg" (reg import "%ProgramData%\GameClub\backup\hklm-dotnet-wow.reg") else (
  reg delete "HKLM\SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v4.0.30319" /v SchUseStrongCrypto /f >nul 2>&1
  reg delete "HKLM\SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v4.0.30319" /v SystemDefaultTlsVersions /f >nul 2>&1
)
if exist "%ProgramData%\GameClub\backup\hklm-winhttp.reg" (reg import "%ProgramData%\GameClub\backup\hklm-winhttp.reg") else (
  reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp" /v DefaultSecureProtocols /f >nul 2>&1
)
if exist "%ProgramData%\GameClub\backup\hklm-winhttp-wow.reg" (reg import "%ProgramData%\GameClub\backup\hklm-winhttp-wow.reg") else (
  reg delete "HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp" /v DefaultSecureProtocols /f >nul 2>&1
)
if exist "%ProgramData%\GameClub\backup\hklm-schannel.reg" (reg import "%ProgramData%\GameClub\backup\hklm-schannel.reg") else (
  reg delete "HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client" /v Enabled /f >nul 2>&1
  reg delete "HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client" /v DisabledByDefault /f >nul 2>&1
)
if exist "%ProgramData%\GameClub\backup\hklm-sw-policies.reg" (reg import "%ProgramData%\GameClub\backup\hklm-sw-policies.reg") else (
  reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR" /v AllowGameDVR /f >nul 2>&1
  reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}" /v Deny_Execute /f >nul 2>&1
)
if exist "%ProgramData%\GameClub\backup\hklm-winlogon.reg" (reg import "%ProgramData%\GameClub\backup\hklm-winlogon.reg") else (
  reg delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v AutoAdminLogon /f >nul 2>&1
  reg delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v DefaultUserName /f >nul 2>&1
  reg delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v DefaultDomainName /f >nul 2>&1
  reg delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v ForceAutoLogon /f >nul 2>&1
  reg delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v DontDisplayLastUserName /f >nul 2>&1
  if exist "%ProgramData%\GameClub\admin.account" call :DeleteHiddenAdmin
)

if not "%PROFILE%"=="" (
  reg load "HKU\GameClubKiosk" "%PROFILE%\NTUSER.DAT" >nul
  if not errorlevel 1 (
    if exist "%ProgramData%\GameClub\backup\hkcu.reg" (
      reg import "%ProgramData%\GameClub\backup\hkcu.reg"
    ) else (
      reg delete "HKU\GameClubKiosk\Software\Microsoft\Windows NT\CurrentVersion\Winlogon" /v Shell /f >nul 2>&1
      reg delete "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableTaskMgr /f >nul 2>&1
      reg delete "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableRegistryTools /f >nul 2>&1
      reg delete "HKU\GameClubKiosk\Software\Policies\Microsoft\Windows\System" /v DisableCMD /f >nul 2>&1
      reg delete "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableLockWorkstation /f >nul 2>&1
      reg delete "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableChangePassword /f >nul 2>&1
      reg delete "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoWinKeys /f >nul 2>&1
      reg delete "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoRun /f >nul 2>&1
      reg delete "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoControlPanel /f >nul 2>&1
      reg delete "HKU\GameClubKiosk\Control Panel\Accessibility\StickyKeys" /v Flags /f >nul 2>&1
      reg delete "HKU\GameClubKiosk\Control Panel\Accessibility\Keyboard Response" /v Flags /f >nul 2>&1
      reg delete "HKU\GameClubKiosk\Control Panel\Accessibility\ToggleKeys" /v Flags /f >nul 2>&1
      reg delete "HKU\GameClubKiosk\System\GameConfigStore" /v GameDVR_Enabled /f >nul 2>&1
      reg delete "HKU\GameClubKiosk\Software\Microsoft\Windows\Windows Error Reporting" /v DontShowUI /f >nul 2>&1
    )
    reg unload "HKU\GameClubKiosk" >nul
  ) else (
    echo Could not load the user hive. Log off %KIOSKUSER% and run uninstall again.
    exit /b 5
  )
)

if "%REMOVEUSER%"=="1" net user "%KIOSKUSER%" /delete

echo Uninstalled. The per-user shell value was removed so the machine default applies.
echo Shell Launcher and Assigned Access were not configured.
exit /b 0

:DeleteHiddenAdmin
set /p ADMINACCOUNT=<"%ProgramData%\GameClub\admin.account"
reg delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList" /v "%ADMINACCOUNT%" /f >nul 2>&1
exit /b 0
