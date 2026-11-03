@echo off
setlocal EnableExtensions
REM Safe Mode or any elevated administrator session.
REM The kiosk user must be logged off so NTUSER.DAT can be loaded.
REM This restores the registry backups taken before install, removes the per-user shell,
REM deletes the guard service, and clears the autologon secret.
REM It does not hide the boot menu, and it does not register the guard under SafeBoot.
REM If the hive cannot be loaded, uninstall exits before changing anything.

echo Game Club recovery. Run this from an elevated prompt in Safe Mode, signed in as an administrator.
echo Log off the kiosk user first. A failed hive load changes nothing.
call "%~dp0uninstall-kiosk.cmd" %*
exit /b %ERRORLEVEL%
