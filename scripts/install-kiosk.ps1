#requires -Version 2
<#
.SYNOPSIS
  Installs the Game Club kiosk on Windows 7 Home and Windows 10 Home (also 8.1 and 11 Home).
  PowerShell 2.0 compatible. No Group Policy editor and no edition-limited application control.
  Lockdown is the per-user Winlogon shell, registry policies those binaries honor, and the guard.
  Every changed registry key is exported first. A failed write imports those backups.
#>
param(
  [string]$User = "GameClubKiosk",
  [string]$ShellPath = "",
  [string]$UserPassFile = "",
  [string]$AdminPassFile = "",
  [string]$GuardPath = "",
  [string]$AdminAccount = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
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
  Write-Error "Install .NET Framework 4.8 first. On Windows 7 also install KB4490628 (servicing stack), KB4474419 (SHA-2), and the cafe root certificate."
}
if ($product -like "*Windows 7*") {
  Write-Output "Windows 7: enabling TLS 1.2. A signed installer needs the SHA-2 update KB4474419 and the servicing stack KB4490628."
}

$existing = Get-WmiObject Win32_UserAccount -Filter ("LocalAccount=True AND Name='" + $User + "'")
if ($existing -eq $null) {
  if ($UserPassFile -eq "") { Write-Error "Pass -UserPassFile to create the kiosk user." }
  $userPass = Get-Content -LiteralPath $UserPassFile | Select-Object -First 1
  & net.exe user $User /add /passwordchg:no /expires:never
  if ($LASTEXITCODE -ne 0) { Write-Error "net user failed." }
  $created = [ADSI]("WinNT://./" + $User + ",user")
  $created.SetPassword($userPass)
  $created.SetInfo()
}
$accountFlags = [ADSI]("WinNT://./" + $User + ",user")
$accountFlags.UserFlags = ([int]$accountFlags.UserFlags.Value) -bor 65536
$accountFlags.SetInfo()

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

$backup = $env:ProgramData + "\GameClub\backup"
if (-not (Test-Path $backup)) { New-Item -ItemType Directory -Path $backup | Out-Null }
function Export-Hive($key, $file) {
  & reg.exe export $key ($backup + "\" + $file) /y | Out-Null
}
Export-Hive "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies" "hklm-policies.reg"
Export-Hive "HKLM\SOFTWARE\Microsoft\.NETFramework" "hklm-dotnet.reg"
Export-Hive "HKLM\SOFTWARE\Wow6432Node\Microsoft\.NETFramework" "hklm-dotnet-wow.reg"
Export-Hive "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp" "hklm-winhttp.reg"
Export-Hive "HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp" "hklm-winhttp-wow.reg"
Export-Hive "HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols" "hklm-schannel.reg"
Export-Hive "HKLM\SOFTWARE\Policies\Microsoft\Windows" "hklm-sw-policies.reg"
Export-Hive "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" "hklm-winlogon.reg"

$hive = "HKU\GameClubKiosk"
$loaded = $false
$secretSet = $false
function Set-Dword($key, $name, $data) {
  & reg.exe add $key /v $name /t REG_DWORD /d $data /f | Out-Null
  if ($LASTEXITCODE -ne 0) { throw ("reg add failed for " + $name) }
}
function Set-String($key, $name, $data) {
  & reg.exe add $key /v $name /t REG_SZ /d $data /f | Out-Null
  if ($LASTEXITCODE -ne 0) { throw ("reg add failed for " + $name) }
}
function Import-BackupFile($file) {
  $path = $backup + "\" + $file
  if (Test-Path $path) { & reg.exe import $path | Out-Null }
}
function Restore-Install {
  if ($loaded) { & reg.exe unload $hive | Out-Null }
  Import-BackupFile "hklm-policies.reg"
  Import-BackupFile "hklm-dotnet.reg"
  Import-BackupFile "hklm-dotnet-wow.reg"
  Import-BackupFile "hklm-winhttp.reg"
  Import-BackupFile "hklm-winhttp-wow.reg"
  Import-BackupFile "hklm-schannel.reg"
  Import-BackupFile "hklm-sw-policies.reg"
  Import-BackupFile "hklm-winlogon.reg"
  if (Test-Path ($backup + "\hkcu.reg")) {
    & reg.exe load $hive ($profile + "\NTUSER.DAT") | Out-Null
    & reg.exe import ($backup + "\hkcu.reg") | Out-Null
    & reg.exe unload $hive | Out-Null
  }
  if ($secretSet) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File ($scriptDir + "\lsa-secret.ps1") -SecretName DefaultPassword -Clear | Out-Null
  }
}

try {
  & reg.exe unload $hive | Out-Null
  & reg.exe load $hive ($profile + "\NTUSER.DAT")
  if ($LASTEXITCODE -ne 0) { Write-Error "Could not load NTUSER.DAT. Nothing was changed. Log the kiosk user off." }
  $loaded = $true
  & reg.exe export $hive ($backup + "\hkcu.reg") /y | Out-Null

  Set-String ($hive + "\Software\Microsoft\Windows NT\CurrentVersion\Winlogon") "Shell" ('"' + $ShellPath + '"')
  Set-Dword ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\System") "DisableTaskMgr" "1"
  Set-Dword ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\System") "DisableRegistryTools" "1"
  Set-Dword ($hive + "\Software\Policies\Microsoft\Windows\System") "DisableCMD" "1"
  Set-Dword ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\System") "DisableLockWorkstation" "1"
  Set-Dword ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\System") "DisableChangePassword" "1"
  Set-Dword ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer") "NoWinKeys" "1"
  Set-Dword ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer") "NoRun" "1"
  Set-Dword ($hive + "\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer") "NoControlPanel" "1"
  Set-String ($hive + "\Control Panel\Accessibility\StickyKeys") "Flags" "506"
  Set-String ($hive + "\Control Panel\Accessibility\Keyboard Response") "Flags" "122"
  Set-String ($hive + "\Control Panel\Accessibility\ToggleKeys") "Flags" "58"
  Set-Dword ($hive + "\System\GameConfigStore") "GameDVR_Enabled" "0"
  Set-Dword ($hive + "\Software\Microsoft\Windows\Windows Error Reporting") "DontShowUI" "1"
  & reg.exe unload $hive | Out-Null
  $loaded = $false

  Set-Dword "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" "HideFastUserSwitching" "1"
  Set-Dword "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" "ConsentPromptBehaviorUser" "0"
  Set-Dword "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" "NoDriveTypeAutoRun" "255"
  Set-Dword "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" "NoAutorun" "1"
  Set-Dword "HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR" "AllowGameDVR" "0"
  Set-Dword "HKLM\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}" "Deny_Execute" "1"
  Set-Dword "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" "DontDisplayLastUserName" "1"

  Set-Dword "HKLM\SOFTWARE\Microsoft\.NETFramework\v4.0.30319" "SchUseStrongCrypto" "1"
  Set-Dword "HKLM\SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v4.0.30319" "SchUseStrongCrypto" "1"
  Set-Dword "HKLM\SOFTWARE\Microsoft\.NETFramework\v4.0.30319" "SystemDefaultTlsVersions" "1"
  Set-Dword "HKLM\SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v4.0.30319" "SystemDefaultTlsVersions" "1"
  Set-Dword "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp" "DefaultSecureProtocols" "2560"
  Set-Dword "HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp" "DefaultSecureProtocols" "2560"
  Set-Dword "HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client" "Enabled" "1"
  Set-Dword "HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client" "DisabledByDefault" "0"

  if ($UserPassFile -ne "") {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File ($scriptDir + "\lsa-secret.ps1") -SecretName DefaultPassword -SecretFile $UserPassFile
    if ($LASTEXITCODE -ne 0) { throw "Could not store the autologon secret." }
    $secretSet = $true
    Remove-Item -LiteralPath $UserPassFile -Force -ErrorAction SilentlyContinue
    Set-Dword "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" "AutoAdminLogon" "1"
    Set-String "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" "DefaultUserName" $User
    Set-String "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" "DefaultDomainName" "."
    Set-Dword "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" "ForceAutoLogon" "1"
  }
  if ($AdminAccount -ne "") {
    Set-Dword ("HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList") $AdminAccount "0"
    $dirEarly = $env:ProgramData + "\GameClub"
    if (-not (Test-Path $dirEarly)) { New-Item -ItemType Directory -Path $dirEarly | Out-Null }
    Set-Content -Path ($dirEarly + "\admin.account") -Value $AdminAccount
  }
}
catch {
  Restore-Install
  Write-Error $_
}

$dir = $env:ProgramData + "\GameClub"
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
& icacls.exe $dir /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" ($User + ":(OI)(CI)M") | Out-Null
Set-Content -Path ($dir + "\shell.path") -Value $ShellPath
Set-Content -Path ($dir + "\kiosk.sid") -Value $sid

if ($GuardPath -ne "") {
  & sc.exe stop GameClubKioskGuard | Out-Null
  & sc.exe delete GameClubKioskGuard | Out-Null
  & sc.exe create GameClubKioskGuard binPath= ("`"" + $GuardPath + "`"") start= auto | Out-Null
  & sc.exe description GameClubKioskGuard "Restarts the kiosk shell and stops escape processes." | Out-Null
  & sc.exe failure GameClubKioskGuard reset= 86400 actions= restart/2000/restart/5000/restart/30000 | Out-Null
  & sc.exe failureflag GameClubKioskGuard 1 | Out-Null
  if ($AdminPassFile -ne "") {
    & $GuardPath "hash-admin-file" $AdminPassFile ($dir + "\admin-gate.json")
    & icacls.exe ($dir + "\admin-gate.json") /inheritance:r /grant:r "SYSTEM:F" "*S-1-5-32-544:F" | Out-Null
    Remove-Item -LiteralPath $AdminPassFile -Force -ErrorAction SilentlyContinue
  }
  & sc.exe start GameClubKioskGuard | Out-Null
}

$cafeRoot = $scriptDir + "\CafeRoot.cer
if (Test-Path $cafeRoot) { & certutil.exe -addstore -f Root $cafeRoot | Out-Null }
Write-Output "Installed. Admin escape is Ctrl+Shift+F12 inside the shell."
Write-Output "Shell Launcher and Assigned Access are not used."
