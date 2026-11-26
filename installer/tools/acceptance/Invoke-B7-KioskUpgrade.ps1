param(
    [string] $LowKioskSetup,
    [string] $HighKioskSetup
)

$ErrorActionPreference = "Stop"
Write-Host "B-7: kiosk upgrade"

$setupPath = "C:\ProgramData\Checkpoint\kiosk\setup.json"
if (Test-Path $setupPath) {
    $prior = Get-Content $setupPath -Raw
} else {
    $prior = $null
    & (Join-Path $PSScriptRoot "Invoke-B5-KioskInstall.ps1") -KioskSetup $LowKioskSetup -LogsDir $env:TEMP
    Set-Content -LiteralPath $setupPath -Value '{"serverBase":"http://127.0.0.1:5000","completedAtUtc":"2026-01-01T00:00:00Z"}'
}

$p = Start-Process -FilePath $HighKioskSetup -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") -Wait -PassThru
if ($p.ExitCode -notin 0, 3010) { throw "B-7: upgrade exit $($p.ExitCode)" }
if (-not (Test-Path $setupPath)) { throw "B-7: setup.json missing after upgrade" }
$setup = Get-Content $setupPath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($setup.serverBase)) { throw "B-7: setup.json serverBase missing" }
$guard = Get-Service CheckpointGuard -ErrorAction SilentlyContinue
if (-not $guard) { throw "B-7: CheckpointGuard missing after upgrade" }
if ($guard.Status -ne "Running") { Start-InstallerCheckpointGuard }
$kioskUser = Get-LocalUser -Name "CheckpointKiosk" -ErrorAction SilentlyContinue
if (-not $kioskUser) { throw "B-7: CheckpointKiosk user missing after upgrade" }
Write-Host "B-7: ok serverBase=$($setup.serverBase)"