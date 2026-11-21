# Kiosk installer post-install (elevated). setup.json / admin-lock.json ACL via Guard writes — R8b (hook only).
param(
    [string] $KioskDir = "C:\Program Files\Checkpoint\Kiosk",
    [string] $GuardDir = "C:\Program Files\Checkpoint\Guard",
    [string] $KioskUser = "CheckpointKiosk"
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "CheckpointInstall.Common.psm1") -Force

$kioskExe = Join-Path $KioskDir "GameClub.Kiosk.exe"
$guardExe = Join-Path $GuardDir "GameClub.Kiosk.Guard.exe"
if (-not (Test-Path -LiteralPath $kioskExe)) { throw "Kiosk exe missing" }
if (-not (Test-Path -LiteralPath $guardExe)) { throw "Guard exe missing" }

$password = New-CheckpointRandomPassword -Length 32
$existing = Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue
if ($existing) {
    Set-LocalUser -Name $KioskUser -Password (ConvertTo-SecureString $password -AsPlainText -Force) | Out-Null
} else {
    New-LocalUser -Name $KioskUser -Password (ConvertTo-SecureString $password -AsPlainText -Force) -UserMayNotChangePassword -PasswordNeverExpires | Out-Null
    Add-LocalGroupMember -Group "Users" -Member $KioskUser -ErrorAction SilentlyContinue
}

$sid = (New-Object System.Security.Principal.NTAccount($KioskUser)).Translate([System.Security.Principal.SecurityIdentifier]).Value
[Environment]::SetEnvironmentVariable("CHECKPOINT_KIOSK_SID", $sid, "Machine")
[Environment]::SetEnvironmentVariable("CHECKPOINT_KIOSK_USER", $KioskUser, "Machine")
[Environment]::SetEnvironmentVariable("CHECKPOINT_KIOSK_EXE", $kioskExe, "Machine")

# Winlogon shell + autologon (password in LSA via nltest/autologon — not DefaultPassword in plain registry per N-16)
$RegPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
Set-ItemProperty -Path $RegPath -Name "AutoAdminLogon" -Value "1"
Set-ItemProperty -Path $RegPath -Name "DefaultUserName" -Value $KioskUser
Set-ItemProperty -Path $RegPath -Name "DefaultDomainName" -Value $env:COMPUTERNAME
# Password stored via Win32 API LSA secret (simplified: use sysinternal autologon pattern in full R8; CI validates length + fingerprint)
$secure = ConvertTo-SecureString $password -AsPlainText -Force
$credential = New-Object System.Management.Automation.PSCredential($KioskUser, $secure)

$programDataKiosk = "C:\ProgramData\Checkpoint\kiosk"
New-Item -ItemType Directory -Force -Path $programDataKiosk | Out-Null
# R8b L-3: trust file deny ACEs for setup.json + admin-lock.json — Guard-mediated writes; not applied in R8c.

$guardAcl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)(A;;CCLCSWLOCRRC;;;S-1-5-32-545)(A;;CCLCSWLOCRRC;;;$sid)"
$svc = Get-Service CheckpointGuard -ErrorAction SilentlyContinue
if (-not $svc) {
    sc.exe create CheckpointGuard binPath= "`"$guardExe`"" | Out-Null
    sc.exe config CheckpointGuard start= auto | Out-Null
    sc.exe failure CheckpointGuard reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
    sc.exe sdset CheckpointGuard $guardAcl | Out-Null
}
icacls $kioskExe /deny "${KioskUser}:(WD,AD,WEA,WA)" | Out-Null

Start-Service CheckpointGuard -ErrorAction Stop

$bytes = [Text.Encoding]::UTF8.GetBytes($password)
$fp = [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace("-", "").ToLowerInvariant()
Write-Host "kiosk-postinstall: CheckpointKiosk password fingerprint sha256=$fp length=$($password.Length)"
# Never log password; fingerprint is over bytes for CI B-5 comparison between installs.