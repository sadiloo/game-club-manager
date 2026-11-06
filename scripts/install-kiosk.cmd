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
if /I "%~1"=="/adminuser" set "ADMINACCOUNT=%~2" & shift & shift & goto parse
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

set "SECRETSET=0"
set "RELEASE=0"
for /f "tokens=3" %%A in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2^>nul') do set "RELEASE=%%A"
set /a RELNUM=%RELEASE% 2>nul
if %RELNUM% LSS 528040 (
  echo .NET Framework 4.8 or newer is required. On Windows 7, install KB4474419 ^(SHA-2^) and KB3140245 ^(TLS 1.2^) first.
  exit /b 3
)
echo %PRODUCT% | findstr /I /C:"Windows 7" >nul
if not errorlevel 1 (
  echo Windows 7: install KB4490628 ^(servicing stack^), KB4474419 ^(SHA-2^), and the cafe root certificate before .NET Framework 4.8.
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
powershell -NoProfile -Command "$u = [ADSI]('WinNT://./%KIOSKUSER%,user'); $u.UserFlags = ([int]$u.UserFlags.Value) -bor 65536; $u.SetInfo()"
if errorlevel 1 (
  echo Could not set PasswordNeverExpires with ADSI.
  exit /b 4
)

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

if not exist "%ProgramData%\GameClub\backup" mkdir "%ProgramData%\GameClub\backup"
call :ExportHive "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies" "hklm-policies.reg"
call :ExportHive "HKLM\SOFTWARE\Microsoft\.NETFramework" "hklm-dotnet.reg"
call :ExportHive "HKLM\SOFTWARE\Wow6432Node\Microsoft\.NETFramework" "hklm-dotnet-wow.reg"
call :ExportHive "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp" "hklm-winhttp.reg"
call :ExportHive "HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp" "hklm-winhttp-wow.reg"
call :ExportHive "HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols" "hklm-schannel.reg"
call :ExportHive "HKLM\SOFTWARE\Policies\Microsoft\Windows" "hklm-sw-policies.reg"
call :ExportHive "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" "hklm-winlogon.reg"
reg unload "HKU\GameClubKiosk" >nul 2>&1
reg load "HKU\GameClubKiosk" "%PROFILE%\NTUSER.DAT" >nul
if errorlevel 1 (
  echo Could not load NTUSER.DAT. The kiosk user must be logged off. Nothing was changed.
  exit /b 5
)
reg export "HKU\GameClubKiosk" "%ProgramData%\GameClub\backup\hkcu.reg" /y >nul 2>&1

reg add "HKU\GameClubKiosk\Software\Microsoft\Windows NT\CurrentVersion\Winlogon" /v Shell /t REG_SZ /d "\"%SHELLPATH%\"" /f >nul || goto :rollback
reg add "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableTaskMgr /t REG_DWORD /d 1 /f >nul || goto :rollback
reg add "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableRegistryTools /t REG_DWORD /d 1 /f >nul || goto :rollback
reg add "HKU\GameClubKiosk\Software\Policies\Microsoft\Windows\System" /v DisableCMD /t REG_DWORD /d 1 /f >nul || goto :rollback
reg add "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableLockWorkstation /t REG_DWORD /d 1 /f >nul || goto :rollback
reg add "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableChangePassword /t REG_DWORD /d 1 /f >nul || goto :rollback
reg add "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoWinKeys /t REG_DWORD /d 1 /f >nul || goto :rollback
reg add "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoRun /t REG_DWORD /d 1 /f >nul || goto :rollback
reg add "HKU\GameClubKiosk\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoControlPanel /t REG_DWORD /d 1 /f >nul || goto :rollback
reg add "HKU\GameClubKiosk\Control Panel\Accessibility\StickyKeys" /v Flags /t REG_SZ /d 506 /f >nul || goto :rollback
reg add "HKU\GameClubKiosk\Control Panel\Accessibility\Keyboard Response" /v Flags /t REG_SZ /d 122 /f >nul || goto :rollback
reg add "HKU\GameClubKiosk\Control Panel\Accessibility\ToggleKeys" /v Flags /t REG_SZ /d 58 /f >nul || goto :rollback
reg add "HKU\GameClubKiosk\System\GameConfigStore" /v GameDVR_Enabled /t REG_DWORD /d 0 /f >nul || goto :rollback
reg add "HKU\GameClubKiosk\Software\Microsoft\Windows\Windows Error Reporting" /v DontShowUI /t REG_DWORD /d 1 /f >nul || goto :rollback
reg unload "HKU\GameClubKiosk" >nul

reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v HideFastUserSwitching /t REG_DWORD /d 1 /f >nul || goto :rollback
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v ConsentPromptBehaviorUser /t REG_DWORD /d 0 /f >nul || goto :rollback
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoDriveTypeAutoRun /t REG_DWORD /d 255 /f >nul || goto :rollback
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoAutorun /t REG_DWORD /d 1 /f >nul || goto :rollback
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR" /v AllowGameDVR /t REG_DWORD /d 0 /f >nul || goto :rollback
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}" /v Deny_Execute /t REG_DWORD /d 1 /f >nul || goto :rollback
reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v DontDisplayLastUserName /t REG_DWORD /d 1 /f >nul || goto :rollback

REM TLS 1.2. Required on Windows 7. 2560 is 0xA00. Uninstall restores the exported values.
reg add "HKLM\SOFTWARE\Microsoft\.NETFramework\v4.0.30319" /v SchUseStrongCrypto /t REG_DWORD /d 1 /f >nul || goto :rollback
reg add "HKLM\SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v4.0.30319" /v SchUseStrongCrypto /t REG_DWORD /d 1 /f >nul || goto :rollback
reg add "HKLM\SOFTWARE\Microsoft\.NETFramework\v4.0.30319" /v SystemDefaultTlsVersions /t REG_DWORD /d 1 /f >nul || goto :rollback
reg add "HKLM\SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v4.0.30319" /v SystemDefaultTlsVersions /t REG_DWORD /d 1 /f >nul || goto :rollback
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp" /v DefaultSecureProtocols /t REG_DWORD /d 2560 /f >nul || goto :rollback
reg add "HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp" /v DefaultSecureProtocols /t REG_DWORD /d 2560 /f >nul || goto :rollback
reg add "HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client" /v Enabled /t REG_DWORD /d 1 /f >nul || goto :rollback
reg add "HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client" /v DisabledByDefault /t REG_DWORD /d 0 /f >nul || goto :rollback

if not "%USERPASSFILE%"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0lsa-secret.ps1" -SecretName DefaultPassword -SecretFile "%USERPASSFILE%"
  if errorlevel 1 goto :rollback
  set "SECRETSET=1"
  del /f /q "%USERPASSFILE%" >nul 2>&1
  reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v AutoAdminLogon /t REG_DWORD /d 1 /f >nul || goto :rollback
  reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v DefaultUserName /t REG_SZ /d "%KIOSKUSER%" /f >nul || goto :rollback
  reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v DefaultDomainName /t REG_SZ /d "." /f >nul || goto :rollback
  reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v ForceAutoLogon /t REG_DWORD /d 1 /f >nul || goto :rollback
)
if not "%ADMINACCOUNT%"=="" (
  reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList" /v "%ADMINACCOUNT%" /t REG_DWORD /d 0 /f >nul || goto :rollback
  if not exist "%ProgramData%\GameClub" mkdir "%ProgramData%\GameClub"
  >"%ProgramData%\GameClub\admin.account" echo %ADMINACCOUNT%
)

if not exist "%ProgramData%\GameClub" mkdir "%ProgramData%\GameClub"
icacls "%ProgramData%\GameClub" /inheritance:r /grant:r SYSTEM:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F "%KIOSKUSER%":(OI)(CI)M >nul
>"%ProgramData%\GameClub\shell.path" echo %SHELLPATH%
>"%ProgramData%\GameClub\kiosk.sid" echo %SID%

if not "%GUARDPATH%"=="" (
  sc stop GameClubKioskGuard >nul 2>&1
  sc delete GameClubKioskGuard >nul 2>&1
  sc create GameClubKioskGuard binPath= "\"%GUARDPATH%\"" start= auto
  sc description GameClubKioskGuard "Restarts the kiosk shell and stops escape processes."
  sc failure GameClubKioskGuard reset= 86400 actions= restart/2000/restart/5000/restart/30000
  sc failureflag GameClubKioskGuard 1
  if not "%ADMINPASSFILE%"=="" (
    if not exist "%ProgramData%\GameClub" mkdir "%ProgramData%\GameClub"
    "%GUARDPATH%" hash-admin-file "%ADMINPASSFILE%" "%ProgramData%\GameClub\admin-gate.json"
    icacls "%ProgramData%\GameClub\admin-gate.json" /inheritance:r /grant:r SYSTEM:F *S-1-5-32-544:F >nul
    del /f /q "%ADMINPASSFILE%" >nul 2>&1
  )
  sc start GameClubKioskGuard >nul 2>&1
)

echo Installed. Kiosk user %KIOSKUSER% starts %SHELLPATH% instead of explorer.exe.
echo Admin escape is Ctrl+Shift+F12 inside the shell, then the admin password.
echo Shell Launcher and Assigned Access are not used. They are not available on these editions.
if exist "%~dp0CafeRoot.cer" certutil -addstore -f Root "%~dp0CafeRoot.cer"
exit /b 0

:rollback
echo Install failed. Restoring the registry backup. Nothing is left half-applied.
reg unload "HKU\GameClubKiosk" >nul 2>&1
call :ImportBackups
if "%SECRETSET%"=="1" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0lsa-secret.ps1" -SecretName DefaultPassword -Clear
exit /b 6

:ExportHive
reg export "%~1" "%ProgramData%\GameClub\backup\%~2" /y >nul 2>&1
exit /b 0

:ImportBackups
if exist "%ProgramData%\GameClub\backup\hklm-policies.reg" reg import "%ProgramData%\GameClub\backup\hklm-policies.reg"
if exist "%ProgramData%\GameClub\backup\hklm-dotnet.reg" reg import "%ProgramData%\GameClub\backup\hklm-dotnet.reg"
if exist "%ProgramData%\GameClub\backup\hklm-dotnet-wow.reg" reg import "%ProgramData%\GameClub\backup\hklm-dotnet-wow.reg"
if exist "%ProgramData%\GameClub\backup\hklm-winhttp.reg" reg import "%ProgramData%\GameClub\backup\hklm-winhttp.reg"
if exist "%ProgramData%\GameClub\backup\hklm-winhttp-wow.reg" reg import "%ProgramData%\GameClub\backup\hklm-winhttp-wow.reg"
if exist "%ProgramData%\GameClub\backup\hklm-schannel.reg" reg import "%ProgramData%\GameClub\backup\hklm-schannel.reg"
if exist "%ProgramData%\GameClub\backup\hklm-sw-policies.reg" reg import "%ProgramData%\GameClub\backup\hklm-sw-policies.reg"
if exist "%ProgramData%\GameClub\backup\hklm-winlogon.reg" reg import "%ProgramData%\GameClub\backup\hklm-winlogon.reg"
if exist "%ProgramData%\GameClub\backup\hkcu.reg" (
  if not "%PROFILE%"=="" (
    reg load "HKU\GameClubKiosk" "%PROFILE%\NTUSER.DAT" >nul 2>&1
    reg import "%ProgramData%\GameClub\backup\hkcu.reg"
    reg unload "HKU\GameClubKiosk" >nul 2>&1
  )
)
exit /b 0

:CreateKioskUser
net user "%KIOSKUSER%" /add /passwordchg:no /expires:never /fullname:"Game Club Kiosk"
if errorlevel 1 exit /b 1
powershell -NoProfile -Command "$p = Get-Content -LiteralPath '%USERPASSFILE%' | Select-Object -First 1; $u = [ADSI]('WinNT://./%KIOSKUSER%,user'); $u.SetPassword($p); $u.SetInfo()"
if errorlevel 1 exit /b 1
exit /b 0
