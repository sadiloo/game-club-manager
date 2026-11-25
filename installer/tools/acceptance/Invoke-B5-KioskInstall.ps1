param(
    [string] $KioskSetup,
    [string] $LogsDir
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null
$log = Join-Path $LogsDir "b5-kiosk-install.log"

Write-Host "B-5: silent kiosk install"
$proc = Start-Process -FilePath $KioskSetup -ArgumentList @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=$log"
) -Wait -PassThru
# Exit 3010 allowed when restart needed
if ($proc.ExitCode -notin 0, 3010) {
    throw "B-5: kiosk setup exit $($proc.ExitCode)"
}
if ($proc.ExitCode -eq 3010) {
    Write-Host "B-5: exit 3010 (restart needed) with /NORESTART as expected"
}
Assert-InnoSetupLogNoFailedRuns -LogPath $log

$user = Get-LocalUser -Name CheckpointKiosk -ErrorAction Stop
if (-not $user) { throw "B-5: CheckpointKiosk user missing" }

$reg = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -ErrorAction Stop
if ($reg.DefaultPassword) {
    throw "B-5: plaintext DefaultPassword in registry"
}

foreach ($name in @("CHECKPOINT_SERVER_URL", "CHECKPOINT_ADMIN_PASSWORD")) {
    $val = [Environment]::GetEnvironmentVariable($name, "Machine")
    if ($val) { throw "B-5: machine env $name must not be set" }
}

Restart-Service CheckpointGuard -ErrorAction Stop
Write-Host "B-5: ok"