# Dot-sourced from ci-installer.ps1 - requires $acceptance, $accounts, $logRoot, setup paths, versions.
# R10e/R10f/R10g leg ledger. ONE table below ($script:AcceptanceLegTable) is the only list of legs. The total is
# derived from that table ($script:AcceptanceExpectedLegCount = the number of rows; R10h: there is NO hand-written
# count any more) and the ordered leg names must match ONE pin before any leg runs: their SHA-256
# ($script:AcceptanceLegNamesSha256), which also fixes the count. In addition every Invoke-*/Reset-* script in the
# acceptance folder must be used by a leg, and no leg name may repeat. Dropping a leg therefore fails (sha), and
# dropping it with the sha recomputed still fails (unused script). Every leg runs through Invoke-AcceptanceLeg, which fails the job unless:
#   - every native call in the leg (and the acceptance scripts it dot-sources) dispositions its exit code before the
#     next native call (AcceptanceNativeExitAudit.ps1): a native failure followed by a native success is not lost;
#   - the leg printed its own ok marker;
#   - the leg printed no skip/skipped/skipping/"not run"/omitted/"n/a" wording, except the allow-listed lines of THAT leg;
#   - the leg left no non-zero exit behind: `exit N` anywhere (also after the ok marker) or a native command's
#     non-zero $LASTEXITCODE that the leg did not consume.
# A failing leg prints "ci-acceptance: LEG FAIL <name> (exit N, phase X)" before the job fails.
# At the end the ledger must list every leg exactly once.
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "AcceptanceNativeExitAudit.ps1")
$script:AcceptanceLedger = [System.Collections.Generic.List[string]]::new()
# R10f item 7: skip wording that fails a leg. "Skipped: 0" in a VSTest summary is not a skip (a skipped test is).
# R10g (rr5 L17): "omitted", "n/a" and "not applicable" are skip wording too.
$script:AcceptanceSkipPattern = '(?i)\bskip(s|ped|ping)?\b|\bnot\s+run\b|\bomit(s|ted|ting)?\b|\bn/a\b|\bnot\s+applicable\b'
$script:AcceptanceNotASkipPattern = '(?i)\bSkipped:\s*0\b'

function Test-AcceptanceSkipLine([string] $Line) {
    $t = $Line -replace $script:AcceptanceNotASkipPattern, ''
    return [bool]($t -match $script:AcceptanceSkipPattern)
}

function Invoke-AcceptanceLeg {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Script,
        [Parameter(Mandatory)][string] $OkPattern,
        [hashtable] $Params = @{},
        # Exact lines this leg (and only this leg) may print although they contain skip wording.
        [string[]] $AllowedSkipLines = @()
    )
    Write-Host "ci-acceptance: LEG BEGIN $Name"
    $phase = "native-audit"
    $global:LASTEXITCODE = 0
    try {
        $legPath = Join-Path $acceptance $Script
        $unchecked = @(Get-AcceptanceDotSourcedScripts $legPath | ForEach-Object { Get-AcceptanceUncheckedNativeCalls $_ })
        if ($unchecked.Count -gt 0) {
            throw "ci-acceptance: LEG $Name has native calls whose exit code is never checked before the next native call (a later success would hide the failure): $($unchecked -join ' | ')"
        }
        $phase = "run"
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $lines = [System.Collections.Generic.List[string]]::new()
        & $legPath @Params *>&1 | ForEach-Object {
            $line = "$_"
            Write-Host $line
            $lines.Add($line)
        }
        $legExit = $global:LASTEXITCODE
        $sw.Stop()
        $phase = "ok-marker"
        $ok = @($lines | Where-Object { $_ -match $OkPattern })
        if ($ok.Count -eq 0) {
            throw "ci-acceptance: LEG $Name finished without its ok marker /$OkPattern/ (vacuous or skipped leg)"
        }
        $phase = "skip"
        $skips = @($lines | Where-Object { (Test-AcceptanceSkipLine $_) -and ($AllowedSkipLines -notcontains $_.Trim()) })
        if ($skips.Count -gt 0) {
            throw "ci-acceptance: LEG $Name printed a skip that is not allow-listed for this leg: $($skips -join ' | ')"
        }
        $phase = "exit-code"
        if ($null -ne $legExit -and $legExit -ne 0) {
            throw "ci-acceptance: LEG $Name left a non-zero exit code $legExit (exit N or an unchecked native command) after printing '$($ok[-1].Trim())'"
        }
    } catch {
        # R10g item 3 (rr5 L-rr5-3): name the failed leg, its exit code and the phase that failed, then fail the job.
        $failExit = if ($null -ne $global:LASTEXITCODE) { $global:LASTEXITCODE } else { 0 }
        Write-Host "ci-acceptance: LEG FAIL $Name (exit $failExit, phase $phase)"
        throw
    }
    $script:AcceptanceLedger.Add($Name)
    Write-Host ("ci-acceptance: LEG OK {0} marker='{1}' seconds={2}" -f $Name, $ok[-1].Trim(), [int]$sw.Elapsed.TotalSeconds)
}

# ---- THE leg list (single source of truth) and its one pin ------------------------------------------------------
# SHA-256 (lower-case hex) of the leg names in table order joined with "\n". Changing the leg list needs this pin too.
# The leg count is NOT written anywhere: it is derived from the table below (R10h, rr6 report honesty).
$script:AcceptanceLegNamesSha256 = "15ff5b7a49b449ef9845da60d3f587f6123de038784b93e62ba8ced4c6264442"
$script:AcceptanceLegTable = @(
    @{ Name = "Reset-1"; Script = "Reset-InstallerAcceptanceMachine.ps1"; OkPattern = '^Reset-InstallerAcceptanceMachine: ok$' }
    @{ Name = "B2-WithoutOpenSessions"; Script = "Invoke-B2-Upgrade.ps1"; OkPattern = '^B-2 \(WithoutOpenSessions\): ok$'; Params = @{
        LowSetup = $serverLow; HighSetup = $serverHigh; AccountsFile = $accounts; LowVersion = $lowVersion; HighVersion = $appVersion; Mode = "WithoutOpenSessions" } }
    @{ Name = "B2-WithOpenSessions"; Script = "Invoke-B2-Upgrade.ps1"; OkPattern = '^B-2 \(WithOpenSessions\): ok$'; Params = @{
        LowSetup = $serverHigh; HighSetup = $serverHigh2; AccountsFile = $accounts; LowVersion = $appVersion; HighVersion = $high2Version; Mode = "WithOpenSessions"; SkipDowngradeCheck = $true } }
    @{ Name = "Reset-2"; Script = "Reset-InstallerAcceptanceMachine.ps1"; OkPattern = '^Reset-InstallerAcceptanceMachine: ok$' }
    @{ Name = "B1"; Script = "Invoke-B1-CleanServerInstall.ps1"; OkPattern = '^B-1: ok$'; Params = @{ ServerSetup = $serverHigh; AccountsFile = $accounts; LogsDir = $logRoot } }
    @{ Name = "B3-BackupNeverDeletes"; Script = "Invoke-B3-BackupNeverDeletes.ps1"; OkPattern = '^B-3: ok dumps=[1-9]'; Params = @{ LogsDir = $logRoot } }
    @{ Name = "B3-BackupFlags"; Script = "Invoke-B3-BackupFlags.ps1"; OkPattern = '^B-3-flags: ok$'; Params = @{ LogsDir = $logRoot; AccountsFile = $accounts } }
    @{ Name = "B3-Restore"; Script = "Invoke-B3-Restore.ps1"; OkPattern = '^B-3-restore: ok dump='; Params = @{ LogsDir = $logRoot } }
    @{ Name = "B4"; Script = "Invoke-B4-UninstallReinstall.ps1"; OkPattern = '^B-4: ok backup='; Params = @{ ServerSetup = $serverHigh; AccountsFile = $accounts } }
    @{ Name = "B5"; Script = "Invoke-B5-KioskInstall.ps1"; OkPattern = '^B-5: ok length='; Params = @{ KioskSetup = $kioskHigh; LogsDir = $logRoot } }
    @{ Name = "B6-B12"; Script = "Invoke-B6-B12-KioskFirstRun.ps1"; OkPattern = '^B-6 \+ B-12: ok$'; Params = @{ RepoRoot = $root; AccountsFile = $accounts } }
    @{ Name = "B7"; Script = "Invoke-B7-KioskUpgrade.ps1"; OkPattern = '^B-7: ok serverBase=\S+ completedAtUtc=\S+ preserved$'; Params = @{ LowKioskSetup = $kioskLow; HighKioskSetup = $kioskHigh; LogsDir = $logRoot } }
    @{ Name = "B8"; Script = "Invoke-B8-WindowsBillingRestart.ps1"; OkPattern = '^B-8: ok ps5='; Params = @{ AccountsFile = $accounts } }
    @{ Name = "B13"; Script = "Invoke-B13-DefaultPinUpgrade.ps1"; OkPattern = '^B-13: ok$'; Params = @{ ServerSetup = $serverHigh; AccountsFile = $accounts } }
    @{ Name = "R8f-PostinstallFail"; Script = "Invoke-R8f-PostinstallFail.ps1"; OkPattern = '^R8f-1: ok$'; Params = @{
        ServerSetupHooks = $serverHighHooks; ServerSetup = $serverHigh; AccountsFile = $accounts; LowSetup = $serverLow
        LowVersion = $lowVersion; HighVersion = $appVersion; LogsDir = $logRoot } }
    # Justified skips (leg audit in the R10e report), allowed ONLY for this leg.
    @{ Name = "R8f-PostinstallPhaseMatrix"; Script = "Invoke-R8f-PostinstallPhaseMatrix.ps1"; OkPattern = '^R8f-phase-matrix: ok$'; Params = @{
        ServerSetupHooks = $serverHighHooks; AccountsFile = $accounts; LowSetup = $serverLow; HighSetup = $serverHigh
        LowVersion = $lowVersion; HighVersion = $appVersion; LogsDir = $logRoot }
       AllowedSkipLines = @(
        "R8f-phase-matrix fresh: skip reinstall-kept-pg (needs kept pgdata; not reachable from a reset machine)",
        "R8f-phase-matrix fresh: skip webview2 (WebView2 runtime already installed on this runner)") }
    @{ Name = "R8f-KioskPostinstallFail"; Script = "Invoke-R8f-KioskPostinstallFail.ps1"; OkPattern = '^R8f-kiosk: ok$'; Params = @{ KioskSetupHooks = $kioskHighHooks; LogsDir = $logRoot } }
    @{ Name = "R8f-ReleaseIgnoresFailPhase"; Script = "Invoke-R8f-ReleaseIgnoresFailPhase.ps1"; OkPattern = '^R8f-release-hook: ok$'; Params = @{ ServerSetup = $serverHigh; AccountsFile = $accounts; LogsDir = $logRoot } }
    @{ Name = "R8f-InvalidAccounts"; Script = "Invoke-R8f-InvalidAccounts.ps1"; OkPattern = '^R8f-invalid-accounts: ok$'; Params = @{ ServerSetup = $serverHigh; LogsDir = $logRoot } }
    @{ Name = "R8f-ExistingPgDataRefuse"; Script = "Invoke-R8f-ExistingPgDataRefuse.ps1"; OkPattern = '^R8f-b: ok$'; Params = @{ ServerSetup = $serverHigh; AccountsFile = $accounts; LogsDir = $logRoot } }
    @{ Name = "Q5-SilentOpenSessions"; Script = "Invoke-Q5-SilentOpenSessions.ps1"; OkPattern = '^Q5: ok$'; Params = @{
        LowSetup = $serverLow; HighSetup = $serverHigh2; AccountsFile = $accounts; LowVersion = $lowVersion; LogsDir = $logRoot } }
    @{ Name = "Q5-InteractiveOpenSessionsProbe"; Script = "Invoke-Q5-InteractiveOpenSessionsProbe.ps1"; OkPattern = '^Q5-interactive: ok count=[1-9]'; Params = @{ LogsDir = $logRoot; AccountsFile = $accounts } }
)
$expectedLegs = @($script:AcceptanceLegTable | ForEach-Object { $_.Name })
$script:AcceptanceExpectedLegCount = $expectedLegs.Count
$dupLegs = @($expectedLegs | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
if ($dupLegs.Count -gt 0) {
    throw "ci-acceptance: leg table has $($expectedLegs.Count) legs with duplicates=[$($dupLegs -join ',')]"
}
$legNamesSha = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($expectedLegs -join "`n"))).ToLowerInvariant()
if ($legNamesSha -ne $script:AcceptanceLegNamesSha256) {
    throw "ci-acceptance: leg names sha256 $legNamesSha does not match the pinned $($script:AcceptanceLegNamesSha256) (leg table has $($expectedLegs.Count) legs: $($expectedLegs -join ','))"
}
$legScripts = @($script:AcceptanceLegTable | ForEach-Object { $_.Script } | Sort-Object -Unique)
$unusedScripts = @(Get-ChildItem -LiteralPath $acceptance -File -Filter "*.ps1" |
    Where-Object { $_.Name -match '^(Invoke|Reset)-' -and $_.Name -ne "Invoke-AcceptanceCommon.ps1" -and $legScripts -notcontains $_.Name } |
    ForEach-Object { $_.Name })
if ($unusedScripts.Count -gt 0) {
    throw "ci-acceptance: acceptance scripts not used by any leg: $($unusedScripts -join ',')"
}
Write-Host "ci-acceptance: leg table $($script:AcceptanceExpectedLegCount) legs (derived from the table), names sha256 $legNamesSha = pinned"

foreach ($leg in $script:AcceptanceLegTable) {
    $legParams = if ($leg.ContainsKey("Params")) { $leg.Params } else { @{} }
    $legSkips = if ($leg.ContainsKey("AllowedSkipLines")) { [string[]]$leg.AllowedSkipLines } else { [string[]]@() }
    Invoke-AcceptanceLeg -Name $leg.Name -Script $leg.Script -OkPattern $leg.OkPattern -Params $legParams -AllowedSkipLines $legSkips
}

Copy-Item -Force "C:\ProgramData\Checkpoint\logs\upgrade.log" (Join-Path $logRoot "upgrade.log") -ErrorAction SilentlyContinue

$ran = @($script:AcceptanceLedger)
$missing = @($expectedLegs | Where-Object { $ran -notcontains $_ })
$extra = @($ran | Where-Object { $expectedLegs -notcontains $_ })
if ($missing.Count -gt 0 -or $extra.Count -gt 0 -or $ran.Count -ne $expectedLegs.Count -or $ran.Count -ne $script:AcceptanceExpectedLegCount) {
    throw "ci-acceptance: ledger mismatch ran=$($ran.Count)/$($script:AcceptanceExpectedLegCount) missing=[$($missing -join ',')] extra=[$($extra -join ',')]"
}
Write-Host "ci-acceptance: LEDGER $($ran.Count)/$($script:AcceptanceExpectedLegCount) legs ok: $($ran -join ', ')"
Write-Host "ci-installer: acceptance done"
