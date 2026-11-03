#requires -Version 2
<#
.SYNOPSIS
  Restores explorer.exe as the kiosk user's Winlogon shell and deletes lockdown values.
  TLS 1.2 settings are kept. PowerShell 2.0 compatible. No gpedit.msc required.
#>
param(
  [string]$User = "GameClubKiosk",
  [switch]$RemoveUser
)

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  Write-Error "Run uninstall-kiosk.ps1 from an elevated PowerShell window."
}

& sc.exe stop GameClubKioskGuard | Out-Null
& sc.exe delete GameClubKioskGuard | Out-Null

$account = New-Object System.Security.Principal.NTAccount($User)
$sid = $account.Translate([System.Security.Principal.SecurityIdentifier]).Value
$profileKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + $sid
if (Test-Path $profileKey) {
  $profile = (Get-ItemProperty $profileKey).ProfileImagePath
  $hive = "HKU\GameClubKiosk"
  & reg.exe load $hive ($profile + "\NTUSER.DAT")
  if ($LASTEXITCODE -ne 0) { Write-Error "Could not load NTUSER.DAT. Log the kiosk user off." }
  & reg.exe add ($hive + "\Software\Microsoft\Windows NT\CurrentVersion\Winlogon") /v Shell /t REG_SZ /d "explorer.exe" /f | Out-Null
  & reg.exe delete ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\System") /v DisableTaskMgr /f | Out-Null
  & reg.exe delete ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\System") /v DisableLockWorkstation /f | Out-Null
  & reg.exe delete ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\System") /v DisableChangePassword /f | Out-Null
  & reg.exe delete ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer") /v NoWinKeys /f | Out-Null
  & reg.exe delete ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer") /v NoRun /f | Out-Null
  & reg.exe delete ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer") /v NoControlPanel /f | Out-Null
  & reg.exe unload $hive | Out-Null
}

& reg.exe delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v HideFastUserSwitching /f | Out-Null
& reg.exe delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoDriveTypeAutoRun /f | Out-Null
& reg.exe delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v NoAutorun /f | Out-Null
& reg.exe delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" /v SettingsPageVisibility /f | Out-Null

if ($RemoveUser) { & net.exe user $User /delete }
Write-Output "Uninstalled. Shell Launcher and Assigned Access were not configured."
