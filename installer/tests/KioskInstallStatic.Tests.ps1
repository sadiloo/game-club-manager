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
    throw "Kiosk PrepareToInstall must set NeedsRestart False"
}
if ($text -match 'NeedsRestart\s*:=\s*True') {
    throw "Kiosk PrepareToInstall must not set NeedsRestart True"
}
if ($text -match 'kiosk-postinstall\.ps1"[^\r\n]*Flags:[^\r\n]*postinstall') {
    throw "Kiosk kiosk-postinstall [Run] must not use postinstall (interactive checkbox can skip mandatory config)"
}
if ($text -notmatch 'Flags:\s*runhidden[^\r\n]*kiosk-postinstall|kiosk-postinstall[^\r\n]*Flags:\s*runhidden') {
    throw "Kiosk kiosk-postinstall [Run] must use Flags: runhidden only (install-phase, like server)"
}
Write-Host "KioskInstallStatic: ok"