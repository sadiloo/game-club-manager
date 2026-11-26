# Dot-sourced from ci-installer.ps1 — requires $acceptance, $accounts, $logRoot, setup paths, versions.
# R10e leg ledger: every leg runs through Invoke-AcceptanceLeg, which fails the job unless the leg printed its own
# "<leg>: ok" marker, and fails on any "skip" line that is not on the explicit allow-list below. At the end the
# ledger must list every expected leg exactly once (no leg can be dropped or skipped silently).
$ErrorActionPreference = "Stop"

$script:AcceptanceLedger = [System.Collections.Generic.List[string]]::new()
# Justified skips (see the leg audit in the R10e report): both are inside R8f-PostinstallPhaseMatrix.
$script:AcceptanceAllowedSkipLines = @(
    "R8f-phase-matrix fresh: skip reinstall-kept-pg (needs kept pgdata; not reachable from a reset machine)",
    "R8f-phase-matrix fresh: skip webview2 (WebView2 runtime already installed on this runner)"
)

function Invoke-AcceptanceLeg {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Script,
        [Parameter(Mandatory)][string] $OkPattern,
        [hashtable] $Params = @{}
    )
    Write-Host "ci-acceptance: LEG BEGIN $Name"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $lines = [System.Collections.Generic.List[string]]::new()
    & (Join-Path $acceptance $Script) @Params *>&1 | ForEach-Object {
        $line = "$_"
        Write-Host $line
        $lines.Add($line)
    }
    $sw.Stop()
    $ok = @($lines | Where-Object { $_ -match $OkPattern })
    if ($ok.Count -eq 0) {
        throw "ci-acceptance: LEG $Name finished without its ok marker /$OkPattern/ (vacuous or skipped leg)"
    }
    $skips = @($lines | Where-Object { $_ -match '(?i)(^|[:;]\s*)skip(ping)?\s' -and $script:AcceptanceAllowedSkipLines -notcontains $_.Trim() })
    if ($skips.Count -gt 0) {
        throw "ci-acceptance: LEG $Name printed a skip that is not allow-listed: $($skips -join ' | ')"
    }
    $script:AcceptanceLedger.Add($Name)
    Write-Host ("ci-acceptance: LEG OK {0} marker='{1}' seconds={2}" -f $Name, $ok[-1].Trim(), [int]$sw.Elapsed.TotalSeconds)
}

$expectedLegs = @(
    "Reset-1", "B2-WithoutOpenSessions", "B2-WithOpenSessions", "Reset-2", "B1", "B3-BackupNeverDeletes", "B3-BackupFlags",
    "B3-Restore", "B4", "B5", "B6-B12", "B7", "B8", "B13", "R8f-PostinstallFail", "R8f-PostinstallPhaseMatrix",
    "R8f-KioskPostinstallFail", "R8f-ReleaseIgnoresFailPhase", "R8f-InvalidAccounts", "R8f-ExistingPgDataRefuse",
    "Q5-SilentOpenSessions", "Q5-InteractiveOpenSessionsProbe"
)

Invoke-AcceptanceLeg -Name "Reset-1" -Script "Reset-InstallerAcceptanceMachine.ps1" -OkPattern '^Reset-InstallerAcceptanceMachine: ok$'
Invoke-AcceptanceLeg -Name "B2-WithoutOpenSessions" -Script "Invoke-B2-Upgrade.ps1" -OkPattern '^B-2 \(WithoutOpenSessions\): ok$' -Params @{
    LowSetup = $serverLow; HighSetup = $serverHigh; AccountsFile = $accounts; LowVersion = $lowVersion; HighVersion = $appVersion; Mode = "WithoutOpenSessions"
}
Invoke-AcceptanceLeg -Name "B2-WithOpenSessions" -Script "Invoke-B2-Upgrade.ps1" -OkPattern '^B-2 \(WithOpenSessions\): ok$' -Params @{
    LowSetup = $serverHigh; HighSetup = $serverHigh2; AccountsFile = $accounts; LowVersion = $appVersion; HighVersion = $high2Version; Mode = "WithOpenSessions"; SkipDowngradeCheck = $true
}
Invoke-AcceptanceLeg -Name "Reset-2" -Script "Reset-InstallerAcceptanceMachine.ps1" -OkPattern '^Reset-InstallerAcceptanceMachine: ok$'
Invoke-AcceptanceLeg -Name "B1" -Script "Invoke-B1-CleanServerInstall.ps1" -OkPattern '^B-1: ok$' -Params @{ ServerSetup = $serverHigh; AccountsFile = $accounts; LogsDir = $logRoot }
Invoke-AcceptanceLeg -Name "B3-BackupNeverDeletes" -Script "Invoke-B3-BackupNeverDeletes.ps1" -OkPattern '^B-3: ok dumps=[1-9]' -Params @{ LogsDir = $logRoot }
Invoke-AcceptanceLeg -Name "B3-BackupFlags" -Script "Invoke-B3-BackupFlags.ps1" -OkPattern '^B-3-flags: ok$' -Params @{ LogsDir = $logRoot; AccountsFile = $accounts }
Invoke-AcceptanceLeg -Name "B3-Restore" -Script "Invoke-B3-Restore.ps1" -OkPattern '^B-3-restore: ok dump=' -Params @{ LogsDir = $logRoot }
Invoke-AcceptanceLeg -Name "B4" -Script "Invoke-B4-UninstallReinstall.ps1" -OkPattern '^B-4: ok backup=' -Params @{ ServerSetup = $serverHigh; AccountsFile = $accounts }
Invoke-AcceptanceLeg -Name "B5" -Script "Invoke-B5-KioskInstall.ps1" -OkPattern '^B-5: ok length=' -Params @{ KioskSetup = $kioskHigh; LogsDir = $logRoot }
Invoke-AcceptanceLeg -Name "B6-B12" -Script "Invoke-B6-B12-KioskFirstRun.ps1" -OkPattern '^B-6 \+ B-12: ok$' -Params @{ RepoRoot = $root; AccountsFile = $accounts }
Invoke-AcceptanceLeg -Name "B7" -Script "Invoke-B7-KioskUpgrade.ps1" -OkPattern '^B-7: ok serverBase=' -Params @{ LowKioskSetup = $kioskLow; HighKioskSetup = $kioskHigh }
Invoke-AcceptanceLeg -Name "B8" -Script "Invoke-B8-WindowsBillingRestart.ps1" -OkPattern '^B-8: ok ps5=' -Params @{ AccountsFile = $accounts }
Invoke-AcceptanceLeg -Name "B13" -Script "Invoke-B13-DefaultPinUpgrade.ps1" -OkPattern '^B-13: ok$' -Params @{ ServerSetup = $serverHigh; AccountsFile = $accounts }

Invoke-AcceptanceLeg -Name "R8f-PostinstallFail" -Script "Invoke-R8f-PostinstallFail.ps1" -OkPattern '^R8f-1: ok$' -Params @{
    ServerSetupHooks = $serverHighHooks; ServerSetup = $serverHigh; AccountsFile = $accounts; LowSetup = $serverLow
    LowVersion = $lowVersion; HighVersion = $appVersion; LogsDir = $logRoot
}
Invoke-AcceptanceLeg -Name "R8f-PostinstallPhaseMatrix" -Script "Invoke-R8f-PostinstallPhaseMatrix.ps1" -OkPattern '^R8f-phase-matrix: ok$' -Params @{
    ServerSetupHooks = $serverHighHooks; AccountsFile = $accounts; LowSetup = $serverLow; HighSetup = $serverHigh
    LowVersion = $lowVersion; HighVersion = $appVersion; LogsDir = $logRoot
}
Invoke-AcceptanceLeg -Name "R8f-KioskPostinstallFail" -Script "Invoke-R8f-KioskPostinstallFail.ps1" -OkPattern '^R8f-kiosk: ok$' -Params @{ KioskSetupHooks = $kioskHighHooks; LogsDir = $logRoot }
Invoke-AcceptanceLeg -Name "R8f-ReleaseIgnoresFailPhase" -Script "Invoke-R8f-ReleaseIgnoresFailPhase.ps1" -OkPattern '^R8f-release-hook: ok$' -Params @{ ServerSetup = $serverHigh; AccountsFile = $accounts; LogsDir = $logRoot }
Invoke-AcceptanceLeg -Name "R8f-InvalidAccounts" -Script "Invoke-R8f-InvalidAccounts.ps1" -OkPattern '^R8f-invalid-accounts: ok$' -Params @{ ServerSetup = $serverHigh; LogsDir = $logRoot }
Invoke-AcceptanceLeg -Name "R8f-ExistingPgDataRefuse" -Script "Invoke-R8f-ExistingPgDataRefuse.ps1" -OkPattern '^R8f-b: ok$' -Params @{ ServerSetup = $serverHigh; AccountsFile = $accounts; LogsDir = $logRoot }
Invoke-AcceptanceLeg -Name "Q5-SilentOpenSessions" -Script "Invoke-Q5-SilentOpenSessions.ps1" -OkPattern '^Q5: ok$' -Params @{
    LowSetup = $serverLow; HighSetup = $serverHigh2; AccountsFile = $accounts; LowVersion = $lowVersion; LogsDir = $logRoot
}
Invoke-AcceptanceLeg -Name "Q5-InteractiveOpenSessionsProbe" -Script "Invoke-Q5-InteractiveOpenSessionsProbe.ps1" -OkPattern '^Q5-interactive: ok count=[1-9]' -Params @{ LogsDir = $logRoot; AccountsFile = $accounts }

Copy-Item -Force "C:\ProgramData\Checkpoint\logs\upgrade.log" (Join-Path $logRoot "upgrade.log") -ErrorAction SilentlyContinue

$ran = @($script:AcceptanceLedger)
$missing = @($expectedLegs | Where-Object { $ran -notcontains $_ })
$extra = @($ran | Where-Object { $expectedLegs -notcontains $_ })
if ($missing.Count -gt 0 -or $extra.Count -gt 0 -or $ran.Count -ne $expectedLegs.Count) {
    throw "ci-acceptance: ledger mismatch ran=$($ran.Count)/$($expectedLegs.Count) missing=[$($missing -join ',')] extra=[$($extra -join ',')]"
}
Write-Host "ci-acceptance: LEDGER $($ran.Count)/$($expectedLegs.Count) legs ok: $($ran -join ', ')"
Write-Host "ci-installer: acceptance done"
