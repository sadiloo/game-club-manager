param(
    [string]$KioskExe = ".\GameClub.Kiosk.exe",
    [string]$GuardExe = ".\GameClub.Kiosk.Guard.exe"
)
# Dev-only: create kiosk user and register guard (no MSI / firewall automation).
Write-Host "Set Winlogon Shell to $KioskExe for the kiosk user (manual policy step documented)."
sc.exe create CheckpointGuard binPath= "`"$GuardExe`""
sc.exe config CheckpointGuard start= auto
