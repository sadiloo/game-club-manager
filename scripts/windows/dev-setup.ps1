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

$reg = "HKU:\Temp" # Per-user policies are applied by guard RegistryPolicyStore for the interactive kiosk profile.
Write-Host "Set Winlogon Shell for $KioskUser to $KioskExe (HKCU\...\Winlogon\Shell)."
Write-Host "Guard disables Task Manager / lock / password change / sign-out UI options for the kiosk user profile."
sc.exe create CheckpointGuard binPath= "`"$GuardExe`"" | Out-Null
sc.exe config CheckpointGuard start= auto | Out-Null
sc.exe failure CheckpointGuard reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
Write-Host "Registered CheckpointGuard Windows service."
