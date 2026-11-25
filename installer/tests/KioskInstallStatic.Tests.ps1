# B-5 static: kiosk .iss has no input pages; no forced shutdown /r
$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$iss = Join-Path $root "installer/kiosk/CheckpointKiosk.iss"
$text = Get-Content -LiteralPath $iss -Raw
if ($text -match 'shutdown\.exe.*/r') {
    throw "Kiosk installer must not force reboot (shutdown /r)"
}
if ($text -notmatch 'DisableWelcomePage\s*=\s*yes') {
    throw "Kiosk installer must keep welcome page disabled (no input pages)"
}
if ($text -notmatch 'NeedsRestart\s*:=\s*False') {
    throw "Kiosk PrepareToInstall must set NeedsRestart False so [Run] postinstall runs on one silent pass"
}
if ($text -match 'NeedsRestart\s*:=\s*True') {
    throw "Kiosk PrepareToInstall must not set NeedsRestart True (Inno 6.7.x skips [Run] when NeedsRestart remains set)"
}
Write-Host "KioskInstallStatic: ok"