#requires -Version 2
<#
.SYNOPSIS
  Restores the registry backups and removes the per-user shell value.
  The hive is loaded before the service is deleted. A load failure changes nothing.
  PowerShell 2.0 compatible. No Group Policy editor required.
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

$account = New-Object System.Security.Principal.NTAccount($User)
$sid = $account.Translate([System.Security.Principal.SecurityIdentifier]).Value
$profileKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + $sid
$profile = $null
if (Test-Path $profileKey) { $profile = (Get-ItemProperty $profileKey).ProfileImagePath }
$hive = "HKU\GameClubKiosk"
if ($profile -ne $null -and $profile -ne "") {
  & reg.exe unload $hive | Out-Null
  & reg.exe load $hive ($profile + "\NTUSER.DAT")
  if ($LASTEXITCODE -ne 0) { Write-Error "Could not load NTUSER.DAT. Nothing was changed. Log the kiosk user off." }
  & reg.exe unload $hive | Out-Null
}

& sc.exe stop GameClubKioskGuard | Out-Null
& sc.exe delete GameClubKioskGuard | Out-Null
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File ($here + "\lsa-secret.ps1") -SecretName DefaultPassword -Clear | Out-Null

$backup = $env:ProgramData + "\GameClub\backup"
function Import-OrDelete($file, $deletes) {
  $path = $backup + "\" + $file
  if (Test-Path $path) {
    & reg.exe import $path | Out-Null
    return
  }
  foreach ($item in $deletes) {
    & reg.exe delete $item.Key /v $item.Name /f | Out-Null
  }
}

Import-OrDelete "hklm-policies.reg" @(
  @{ Key = "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"; Name = "HideFastUserSwitching" },
  @{ Key = "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"; Name = "ConsentPromptBehaviorUser" },
  @{ Key = "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer"; Name = "NoDriveTypeAutoRun" },
  @{ Key = "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer"; Name = "NoAutorun" }
)
Import-OrDelete "hklm-dotnet.reg" @(
  @{ Key = "HKLM\SOFTWARE\Microsoft\.NETFramework\v4.0.30319"; Name = "SchUseStrongCrypto" },
  @{ Key = "HKLM\SOFTWARE\Microsoft\.NETFramework\v4.0.30319"; Name = "SystemDefaultTlsVersions" }
)
Import-OrDelete "hklm-dotnet-wow.reg" @(
  @{ Key = "HKLM\SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v4.0.30319"; Name = "SchUseStrongCrypto" },
  @{ Key = "HKLM\SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v4.0.30319"; Name = "SystemDefaultTlsVersions" }
)
Import-OrDelete "hklm-winhttp.reg" @(
  @{ Key = "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp"; Name = "DefaultSecureProtocols" }
)
Import-OrDelete "hklm-winhttp-wow.reg" @(
  @{ Key = "HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp"; Name = "DefaultSecureProtocols" }
)
Import-OrDelete "hklm-schannel.reg" @(
  @{ Key = "HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client"; Name = "Enabled" },
  @{ Key = "HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client"; Name = "DisabledByDefault" }
)
Import-OrDelete "hklm-sw-policies.reg" @(
  @{ Key = "HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR"; Name = "AllowGameDVR" },
  @{ Key = "HKLM\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}"; Name = "Deny_Execute" }
)
Import-OrDelete "hklm-winlogon.reg" @(
  @{ Key = "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"; Name = "AutoAdminLogon" },
  @{ Key = "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"; Name = "DefaultUserName" },
  @{ Key = "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"; Name = "DefaultDomainName" },
  @{ Key = "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"; Name = "ForceAutoLogon" },
  @{ Key = "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"; Name = "DontDisplayLastUserName" }
)

if ($profile -ne $null -and $profile -ne "") {
  & reg.exe load $hive ($profile + "\NTUSER.DAT")
  if ($LASTEXITCODE -ne 0) { Write-Error "Could not load NTUSER.DAT. Log the kiosk user off." }
  if (Test-Path ($backup + "\hkcu.reg")) {
    & reg.exe import ($backup + "\hkcu.reg") | Out-Null
  } else {
    & reg.exe delete ($hive + "\Software\Microsoft\Windows NT\CurrentVersion\Winlogon") /v Shell /f | Out-Null
    & reg.exe delete ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\System") /v DisableTaskMgr /f | Out-Null
    & reg.exe delete ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\System") /v DisableRegistryTools /f | Out-Null
    & reg.exe delete ($hive + "\Software\Policies\Microsoft\Windows\System") /v DisableCMD /f | Out-Null
    & reg.exe delete ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\System") /v DisableLockWorkstation /f | Out-Null
    & reg.exe delete ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\System") /v DisableChangePassword /f | Out-Null
    & reg.exe delete ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer") /v NoWinKeys /f | Out-Null
    & reg.exe delete ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer") /v NoRun /f | Out-Null
    & reg.exe delete ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer") /v NoControlPanel /f | Out-Null
    & reg.exe delete ($hive + "\Control Panel\Accessibility\StickyKeys") /v Flags /f | Out-Null
    & reg.exe delete ($hive + "\Control Panel\Accessibility\Keyboard Response") /v Flags /f | Out-Null
    & reg.exe delete ($hive + "\Control Panel\Accessibility\ToggleKeys") /v Flags /f | Out-Null
    & reg.exe delete ($hive + "\System\GameConfigStore") /v GameDVR_Enabled /f | Out-Null
    & reg.exe delete ($hive + "\Software\Microsoft\Windows\Windows Error Reporting") /v DontShowUI /f | Out-Null
  }
  & reg.exe unload $hive | Out-Null
}

if ($RemoveUser) { & net.exe user $User /delete }
Write-Output "Uninstalled. The per-user shell value was removed. Shell Launcher and Assigned Access were not configured."
