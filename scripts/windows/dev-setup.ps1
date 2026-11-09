param(
    [string]$KioskUser = "CheckpointKiosk",
    [string]$KioskExe = ".\GameClub.Kiosk.exe",
    [string]$GuardExe = ".\GameClub.Kiosk.Guard.exe"
)
# Dev-only helper: create local kiosk user, set shell, and register guard (no MSI / firewall automation).
$ErrorActionPreference = "Stop"
if (-not (Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue)) {
    $pwd = Read-Host "Temporary kiosk user password" -AsSecureString
    New-LocalUser -Name $KioskUser -Password $pwd -PasswordNeverExpires -AccountNeverExpires | Out-Null
    Write-Host "Created local user $KioskUser"
}

$sid = (New-Object System.Security.Principal.NTAccount($KioskUser)).Translate([System.Security.Principal.SecurityIdentifier]).Value
[Environment]::SetEnvironmentVariable("CHECKPOINT_KIOSK_SID", $sid, "Machine")
[Environment]::SetEnvironmentVariable("CHECKPOINT_KIOSK_USER", $KioskUser, "Machine")
[Environment]::SetEnvironmentVariable("CHECKPOINT_KIOSK_EXE", (Resolve-Path $KioskExe).Path, "Machine")
Write-Host "Configured CHECKPOINT_KIOSK_SID=$sid for Guard registry hive targeting."
Write-Host "Set Winlogon Shell for $KioskUser to $KioskExe (HKCU\...\Winlogon\Shell)."
Write-Host "Guard applies expanded Policies\System DWORD set and session-targeted shell recovery (not a process-name poll)."
$kioskSid = (New-Object System.Security.Principal.NTAccount($KioskUser)).Translate([System.Security.Principal.SecurityIdentifier]).Value
Write-Host "Applying service DACL: kiosk SID $kioskSid may not stop/delete CheckpointGuard."
$guardAcl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)(A;;CCLCSWLOCRRC;;;S-1-5-32-545)(A;;CCLCSWLOCRRC;;;$kioskSid)"
sc.exe create CheckpointGuard binPath= "`"$GuardExe`"" | Out-Null
sc.exe config CheckpointGuard start= auto | Out-Null
sc.exe failure CheckpointGuard reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
sc.exe sdset CheckpointGuard $guardAcl | Out-Null
$exePath = (Resolve-Path $KioskExe).Path
Write-Host "Harden executable ACL with: icacls `"$exePath`" /deny ${KioskUser}:(WD,AD,WEA,WA)"
icacls $exePath /deny "${KioskUser}:(WD,AD,WEA,WA)" | Out-Null
Write-Host "Registered CheckpointGuard Windows service with DACL."
