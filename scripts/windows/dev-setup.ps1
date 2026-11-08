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
Write-Host "Optional: set service/process DACL so kiosk user cannot stop Checkpoint Guard."
sc.exe create CheckpointGuard binPath= "`"$GuardExe`"" | Out-Null
sc.exe config CheckpointGuard start= auto | Out-Null
sc.exe failure CheckpointGuard reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
Write-Host "Registered CheckpointGuard Windows service."
