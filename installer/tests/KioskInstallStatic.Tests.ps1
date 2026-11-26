# B-5 static: kiosk .iss has no input pages; no forced shutdown /r; postinstall via ssPostInstall Exec
$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$iss = Join-Path $root "installer/kiosk/CheckpointKiosk.iss"
$wizard = Join-Path $root "installer/kiosk/KioskWizard.inc.iss"
$text = Get-Content -LiteralPath $iss -Raw
$wiz = Get-Content -LiteralPath $wizard -Raw
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
if ($text -match 'kiosk-postinstall\.ps1[^\r\n]*Flags:[^\r\n]*postinstall') {
    throw "Kiosk kiosk-postinstall must not use [Run] postinstall flag (mandatory config runs from ssPostInstall)"
}
if ($wiz -notmatch 'procedure RunKioskPostinstallPhase') {
    throw "KioskWizard must run kiosk-postinstall from ssPostInstall"
}
if ($wiz -notmatch 'CHECKPOINT_EXIT_FAIL_KIOSK_POSTINSTALL\s*=\s*150') {
    throw "KioskWizard must define exit code 150 for postinstall failure"
}
Write-Host "KioskInstallStatic: ok"