#requires -Version 2
<#
.SYNOPSIS
  Installs the Game Club kiosk on Windows 7 Home and Windows 10 Home (also 8.1 and 11 Home).
  PowerShell 2.0 compatible. No Group Policy editor and no edition-limited application control.
  Lockdown is the per-user Winlogon shell, registry policies those binaries honor, and the guard.
#>
param(
  [string]$User = "GameClubKiosk",
  [string]$ShellPath = "",
  [string]$UserPassFile = "",
  [string]$AdminPassFile = "",
  [string]$GuardPath = ""
)

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  Write-Error "Run install-kiosk.ps1 from an elevated PowerShell window."
}

if ($ShellPath -eq "") {
  Write-Error "Pass -ShellPath."
}

$product = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion").ProductName
Write-Output ("Detected " + $product)
if ($product -like "*Windows 8*" -and $product -notlike "*8.1*") {
  Write-Error "Windows 8 must be upgraded to Windows 8.1. .NET Framework 4.8 does not support Windows 8."
}

$releaseKey = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" -ErrorAction SilentlyContinue
$release = 0
if ($releaseKey -ne $null) { $release = [int]$releaseKey.Release }
if ($release -lt 528040) {
  Write-Error "Install .NET Framework 4.8 first. On Windows 7 also install KB4474419 (SHA-2) and KB3140245 (TLS 1.2)."
}
if ($product -like "*Windows 7*") {
  Write-Output "Windows 7: enabling TLS 1.2. A signed installer needs the SHA-2 update KB4474419."
}

$existing = Get-WmiObject Win32_UserAccount -Filter ("LocalAccount=True AND Name='" + $User + "'")
if ($existing -eq $null) {
  if ($UserPassFile -eq "") { Write-Error "Pass -UserPassFile to create the kiosk user." }
  $userPass = Get-Content -Path $UserPassFile | Select-Object -First 1
  & net.exe user $User $userPass /add /passwordchg:no /expires:never
  if ($LASTEXITCODE -ne 0) { Write-Error "net user failed." }
}

$account = New-Object System.Security.Principal.NTAccount($User)
$sid = $account.Translate([System.Security.Principal.SecurityIdentifier]).Value
$profileKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + $sid
$profile = $null
if (Test-Path $profileKey) {
  $profile = (Get-ItemProperty $profileKey).ProfileImagePath
}
if ($profile -eq $null -or $profile -eq "") {
  $source = @"
[System.Runtime.InteropServices.DllImport("userenv.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
public static extern int CreateProfile(string sid, string userName, System.Text.StringBuilder path, int size);
"@
  Add-Type -Namespace GameClub -Name Native -MemberDefinition $source
  $buffer = New-Object System.Text.StringBuilder 260
  [GameClub.Native]::CreateProfile($sid, $User, $buffer, 260) | Out-Null
  if (Test-Path $profileKey) { $profile = (Get-ItemProperty $profileKey).ProfileImagePath }
}
if ($profile -eq $null -or $profile -eq "") {
  Write-Error "Sign in once as the kiosk user, log off, and run the installer again."
}

$hive = "HKU\GameClubKiosk"
& reg.exe load $hive ($profile + "\NTUSER.DAT")
if ($LASTEXITCODE -ne 0) { Write-Error "Could not load NTUSER.DAT. Log the kiosk user off." }

function Set-Dword($key, $name, $data) {
  & reg.exe add $key /v $name /t REG_DWORD /d $data /f | Out-Null
}
function Set-String($key, $name, $data) {
  & reg.exe add $key /v $name /t REG_SZ /d $data /f | Out-Null
}

Set-String ($hive + "\Software\Microsoft\Windows NT\CurrentVersion\Winlogon") "Shell" $ShellPath
Set-Dword ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\System") "DisableTaskMgr" "1"
Set-Dword ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\System") "DisableRegistryTools" "1"
Set-Dword ($hive + "\Software\Policies\Microsoft\Windows\System") "DisableCMD" "1"
Set-Dword ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\System") "DisableLockWorkstation" "1"
Set-Dword ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\System") "DisableChangePassword" "1"
Set-Dword ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer") "NoWinKeys" "1"
Set-Dword ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer") "NoRun" "1"
Set-Dword ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer") "NoControlPanel" "1"
& reg.exe unload $hive | Out-Null

Set-Dword "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" "HideFastUserSwitching" "1"
Set-Dword "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" "NoDriveTypeAutoRun" "255"
Set-Dword "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" "NoAutorun" "1"
Set-String "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" "SettingsPageVisibility" "hide:allpages"

# TLS 1.2 for .NET Framework. Required on Windows 7. 2560 = 0xA00.
Set-Dword "HKLM\SOFTWARE\Microsoft\.NETFramework\v4.0.30319" "SchUseStrongCrypto" "1"
Set-Dword "HKLM\SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v4.0.30319" "SchUseStrongCrypto" "1"
Set-Dword "HKLM\SOFTWARE\Microsoft\.NETFramework\v4.0.30319" "SystemDefaultTlsVersions" "1"
Set-Dword "HKLM\SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v4.0.30319" "SystemDefaultTlsVersions" "1"
Set-Dword "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp" "DefaultSecureProtocols" "2560"
Set-Dword "HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp" "DefaultSecureProtocols" "2560"
Set-Dword "HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client" "Enabled" "1"
Set-Dword "HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client" "DisabledByDefault" "0"

$dir = $env:ProgramData + "\GameClub"
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
Set-Content -Path ($dir + "\shell.path") -Value $ShellPath

if ($GuardPath -ne "") {
  & sc.exe stop GameClubKioskGuard | Out-Null
  & sc.exe delete GameClubKioskGuard | Out-Null
  & sc.exe create GameClubKioskGuard binPath= ("`"" + $GuardPath + "`"") start= auto | Out-Null
  & sc.exe description GameClubKioskGuard "Restarts the kiosk shell and stops escape processes." | Out-Null
  if ($AdminPassFile -ne "") {
    $dir = $env:ProgramData + "\GameClub"
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    & $GuardPath "hash-admin-file" $AdminPassFile ($dir + "\admin-gate.json")
  }
  & sc.exe start GameClubKioskGuard | Out-Null
}

Write-Output "Installed. Admin escape is Ctrl+Shift+F12 inside the shell."
Write-Output "Shell Launcher and Assigned Access are not used."
