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

# R10i OBS-1 (ruling): completedAtUtc is compared as the RAW JSON text, never as a parsed date. PowerShell 7's
# ConvertFrom-Json turns an ISO string into a DateTime, and [string] of that is culture-formatted to the second (a 0.4 s
# change slipped through and the ok line printed 01/01/2026 00:00:00). System.Text.Json returns the token exactly as
# written. Windows PowerShell 5.1 has no System.Text.Json, but its ConvertFrom-Json keeps strings as written.
function Get-B7RawJsonProperty {
    param([string] $Raw, [string] $Name)
    if (-not ("System.Text.Json.JsonDocument" -as [type])) {
        $j = $Raw | ConvertFrom-Json
        if ($null -eq $j -or -not ($j.PSObject.Properties.Name -contains $Name)) { return [pscustomobject]@{ Present = $false; Token = $null; Text = $null } }
        $v = $j.$Name
        if ($v -isnot [string]) { throw "B-7: $Name must be a JSON string (Windows PowerShell 5.1 path)" }
        return [pscustomobject]@{ Present = $true; Token = ($v | ConvertTo-Json -Compress); Text = $v }
    }
    $doc = [System.Text.Json.JsonDocument]::Parse($Raw)
    try {
        $el = [System.Text.Json.JsonElement]::new()
        if (-not $doc.RootElement.TryGetProperty($Name, [ref]$el)) { return [pscustomobject]@{ Present = $false; Token = $null; Text = $null } }
        $text = if ($el.ValueKind -eq [System.Text.Json.JsonValueKind]::String) { $el.GetString() } else { $el.GetRawText() }
        return [pscustomobject]@{ Present = $true; Token = $el.GetRawText(); Text = $text }
    } finally {
        $doc.Dispose()
    }
}

# R10j (BL-rr8-1) item 1a: on ANY B-7 failure, dump the evidence first (both Inno log tails, sc query/qc/qfailure
# CheckpointGuard, the Guard watchdog log, System SCM + Application crash events), then rethrow the original error.
. (Join-Path $PSScriptRoot "Get-CheckpointGuardDiagnostics.ps1")
function Write-B7FailureDiagnostics {
    param([string[]] $InnoLogs)
    try {
        foreach ($line in (Get-CheckpointGuardDiagnosticLines -InnoLogs $InnoLogs)) { Write-Host "B-7 diag: $line" }
    } catch {
        Write-Host "B-7 diag: diagnostics failed: $($_.Exception.Message)"
    }
}

# R10j item 1d: Guard must be Running IMMEDIATELY when kiosk Setup returns success (product ruling: no 60 s recovery,
# not the B-5 retry helper). One sample: no wait, no start, no restart.
function Assert-B7GuardRunningNow {
    param([string] $When)
    $guard = Get-Service CheckpointGuard -ErrorAction SilentlyContinue
    if (-not $guard) { throw "B-7: CheckpointGuard missing after $When" }
    if ($guard.Status -ne "Running") {
        throw "B-7: CheckpointGuard must be Running after $When; status=$($guard.Status)"
    }
}

$setupPath = "C:\ProgramData\Checkpoint\kiosk\setup.json"

$lowLog = Join-Path $LogsDir "b7-kiosk-low-install.log"
$highLog = Join-Path $LogsDir "b7-kiosk-upgrade.log"
try {
$pLow = Start-Process -FilePath $LowKioskSetup -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=$lowLog") -Wait -PassThru
if ($pLow.ExitCode -notin 0, 3010) { throw "B-7: low kiosk install exit $($pLow.ExitCode)" }
Assert-B7GuardRunningNow -When "low install"
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
$beforeCompleted = Get-B7RawJsonProperty -Raw $before.Raw -Name "completedAtUtc"
if (-not $beforeCompleted.Present) { throw "B-7: setup.json has no completedAtUtc before the upgrade" }

$p = Start-Process -FilePath $HighKioskSetup -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=$highLog") -Wait -PassThru
if ($p.ExitCode -notin 0, 3010) { throw "B-7: upgrade exit $($p.ExitCode)" }
Assert-B7GuardRunningNow -When "upgrade"
Assert-InnoSetupLogNoFailedRuns -LogPath $highLog
Assert-KioskPostinstallCompleted -InnoLogPath $highLog
if (-not (Test-Path $setupPath)) { throw "B-7: setup.json missing after upgrade" }
$after = Read-B7KioskSetup -Path $setupPath
$setup = $after.Json
if ($null -eq $setup -or [string]::IsNullOrWhiteSpace($setup.serverBase)) { throw "B-7: setup.json serverBase missing (state=$($after.State))" }
if ([string]$setup.serverBase -ne $beforeServerBase) { throw "B-7: upgrade changed serverBase '$beforeServerBase' -> '$($setup.serverBase)'" }
$afterCompleted = Get-B7RawJsonProperty -Raw $after.Raw -Name "completedAtUtc"
if (-not $afterCompleted.Present -or -not [string]::Equals($afterCompleted.Token, $beforeCompleted.Token, [StringComparison]::Ordinal)) { throw "B-7: upgrade changed completedAtUtc $($beforeCompleted.Token) -> $($afterCompleted.Token)" }
$kioskUser = Get-LocalUser -Name "CheckpointKiosk" -ErrorAction SilentlyContinue
if (-not $kioskUser) { throw "B-7: CheckpointKiosk user missing after upgrade" }
} catch {
    Write-B7FailureDiagnostics -InnoLogs @($lowLog, $highLog)
    throw
}
Write-Host "B-7: ok serverBase=$($setup.serverBase) completedAtUtc=$($afterCompleted.Text) preserved"
