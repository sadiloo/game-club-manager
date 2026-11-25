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
if ($text -notmatch 'NeedsRestart\s*:=\s*True') {
    throw "Kiosk PrepareToInstall must keep NeedsRestart True (3010 after install when reboot pending; B-5 allows /NORESTART)"
}
Write-Host "KioskInstallStatic: ok"