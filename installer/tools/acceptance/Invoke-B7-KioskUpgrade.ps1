param(
    [string] $LowKioskSetup,
    [string] $HighKioskSetup,
    [string] $LogsDir = $env:TEMP
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
Write-Host "B-7: kiosk upgrade"
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null

# R10h BL-rr6-1 (root cause: harness). B5 leaves the kiosk installed with the EMPTY setup.json placeholder that
# Set-KioskTrustFileAcl (kiosk-postinstall) creates for the ACL; the kiosk reads an empty / invalid file as "not
# configured" (KioskSetupStore.TryLoad -> null -> first-run wizard). The old B7 seeded only when the file was MISSING, so
# it skipped seeding, "upgraded" B5's HIGH kiosk with the same HIGH setup (about 2 s, no /LOG) and then found no
# serverBase. The product keeps an existing setup.json (the placeholder is written only when the file is missing).
# Now B7 always does a real LOW -> HIGH upgrade, seeds a complete config on the LOW install unless one is already
# there, and requires serverBase AND completedAtUtc to be preserved exactly by the upgrade.
function Read-B7KioskSetup {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { return [pscustomobject]@{ State = "missing"; Raw = $null; Json = $null } }
    $raw = [System.IO.File]::ReadAllText($Path)
    if ([string]::IsNullOrWhiteSpace($raw)) { return [pscustomobject]@{ State = "empty_placeholder"; Raw = $raw; Json = $null } }
    try { $json = $raw | ConvertFrom-Json } catch { return [pscustomobject]@{ State = "invalid_json"; Raw = $raw; Json = $null } }
    if ($null -eq $json -or [string]::IsNullOrWhiteSpace([string]$json.serverBase)) { return [pscustomobject]@{ State = "blank_serverBase"; Raw = $raw; Json = $json } }
    return [pscustomobject]@{ State = "configured"; Raw = $raw; Json = $json }
}

$setupPath = "C:\ProgramData\Checkpoint\kiosk\setup.json"

$lowLog = Join-Path $LogsDir "b7-kiosk-low-install.log"
$pLow = Start-Process -FilePath $LowKioskSetup -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=$lowLog") -Wait -PassThru
if ($pLow.ExitCode -notin 0, 3010) { throw "B-7: low kiosk install exit $($pLow.ExitCode)" }
Assert-InnoSetupLogNoFailedRuns -LogPath $lowLog
Assert-KioskPostinstallCompleted -InnoLogPath $lowLog

$before = Read-B7KioskSetup -Path $setupPath
if ($before.State -ne "configured") {
    $seed = '{"serverBase":"http://127.0.0.1:5000","completedAtUtc":"2026-01-01T00:00:00Z"}'
    [System.IO.File]::WriteAllText($setupPath, $seed, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "B-7: seeded setup.json on the low install (was $($before.State))"
    $before = Read-B7KioskSetup -Path $setupPath
    if ($before.State -ne "configured") { throw "B-7: seeding setup.json failed (state=$($before.State))" }
} else {
    Write-Host "B-7: setup.json already configured before the upgrade"
}
$beforeServerBase = [string]$before.Json.serverBase
$beforeCompleted = [string]$before.Json.completedAtUtc

$highLog = Join-Path $LogsDir "b7-kiosk-upgrade.log"
$p = Start-Process -FilePath $HighKioskSetup -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=$highLog") -Wait -PassThru
if ($p.ExitCode -notin 0, 3010) { throw "B-7: upgrade exit $($p.ExitCode)" }
Assert-InnoSetupLogNoFailedRuns -LogPath $highLog
Assert-KioskPostinstallCompleted -InnoLogPath $highLog
if (-not (Test-Path $setupPath)) { throw "B-7: setup.json missing after upgrade" }
$after = Read-B7KioskSetup -Path $setupPath
$setup = $after.Json
if ($null -eq $setup -or [string]::IsNullOrWhiteSpace($setup.serverBase)) { throw "B-7: setup.json serverBase missing (state=$($after.State))" }
if ([string]$setup.serverBase -ne $beforeServerBase) { throw "B-7: upgrade changed serverBase '$beforeServerBase' -> '$($setup.serverBase)'" }
if ([string]$setup.completedAtUtc -ne $beforeCompleted) { throw "B-7: upgrade changed completedAtUtc '$beforeCompleted' -> '$($setup.completedAtUtc)'" }
$guard = Get-Service CheckpointGuard -ErrorAction SilentlyContinue
if (-not $guard) { throw "B-7: CheckpointGuard missing after upgrade" }
if ($guard.Status -ne "Running") {
    throw "B-7: CheckpointGuard must be Running after upgrade; status=$($guard.Status)"
}
$kioskUser = Get-LocalUser -Name "CheckpointKiosk" -ErrorAction SilentlyContinue
if (-not $kioskUser) { throw "B-7: CheckpointKiosk user missing after upgrade" }
Write-Host "B-7: ok serverBase=$($setup.serverBase) completedAtUtc=$($setup.completedAtUtc) preserved"
