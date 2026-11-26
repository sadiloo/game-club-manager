# Self-check for the leg ledger in ci-installer-acceptance.ps1 (R10e, hardened in R10f item 7). Runs the real driver
# against fake leg scripts in a temp dir: all legs ok => passes; a leg without its ok marker, any skip wording that is not
# allow-listed for THAT leg, a non-zero exit (exit N, also after the ok marker, or an unchecked native exit code), a
# throwing leg, or a leg table that no longer has the pinned 22 legs => throws.
$ErrorActionPreference = "Stop"
$driver = Join-Path $PSScriptRoot "ci-installer-acceptance.ps1"
$markers = [ordered]@{
    "Reset-InstallerAcceptanceMachine.ps1"     = "Reset-InstallerAcceptanceMachine: ok"
    "Invoke-B2-Upgrade.ps1"                    = 'B-2 ($Mode): ok'
    "Invoke-B1-CleanServerInstall.ps1"         = "B-1: ok"
    "Invoke-B3-BackupNeverDeletes.ps1"         = "B-3: ok dumps=2"
    "Invoke-B3-BackupFlags.ps1"                = "B-3-flags: ok"
    "Invoke-B3-Restore.ps1"                    = "B-3-restore: ok dump=x.dump ps5_rate=1"
    "Invoke-B4-UninstallReinstall.ps1"         = "B-4: ok backup=x.dump"
    "Invoke-B5-KioskInstall.ps1"               = "B-5: ok length=1"
    "Invoke-B6-B12-KioskFirstRun.ps1"          = "B-6 + B-12: ok"
    "Invoke-B7-KioskUpgrade.ps1"               = "B-7: ok serverBase=http://x"
    "Invoke-B8-WindowsBillingRestart.ps1"      = "B-8: ok ps5=1 pc=1 cap=1"
    "Invoke-B13-DefaultPinUpgrade.ps1"         = "B-13: ok"
    "Invoke-R8f-PostinstallFail.ps1"           = "R8f-1: ok"
    "Invoke-R8f-PostinstallPhaseMatrix.ps1"    = "R8f-phase-matrix: ok"
    "Invoke-R8f-KioskPostinstallFail.ps1"      = "R8f-kiosk: ok"
    "Invoke-R8f-ReleaseIgnoresFailPhase.ps1"   = "R8f-release-hook: ok"
    "Invoke-R8f-InvalidAccounts.ps1"           = "R8f-invalid-accounts: ok"
    "Invoke-R8f-ExistingPgDataRefuse.ps1"      = "R8f-b: ok"
    "Invoke-Q5-SilentOpenSessions.ps1"         = "Q5: ok"
    "Invoke-Q5-InteractiveOpenSessionsProbe.ps1" = "Q5-interactive: ok count=2"
}

function Invoke-LedgerCase {
    param([string] $Label, [hashtable] $Override = @{}, [bool] $ExpectThrow, [string] $ExpectMessage = "", [scriptblock] $DriverTransform = $null)
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("cp-ledger-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    try {
        foreach ($k in $markers.Keys) {
            $body = if ($Override.ContainsKey($k)) { $Override[$k] } else { "param([string]`$Mode, [switch]`$SkipDowngradeCheck, `$LowSetup, `$HighSetup, `$AccountsFile, `$LowVersion, `$HighVersion, `$LogsDir, `$ServerSetup, `$KioskSetup, `$RepoRoot, `$LowKioskSetup, `$HighKioskSetup, `$ServerSetupHooks, `$KioskSetupHooks)`nWrite-Host `"$($markers[$k])`"" }
            Set-Content -LiteralPath (Join-Path $dir $k) -Value $body
        }
        $driverPath = $driver
        if ($DriverTransform) {
            $src = Get-Content -LiteralPath $driver -Raw
            $mut = & $DriverTransform $src
            if ($mut -eq $src) { throw "ledger self-check ${Label}: driver transform made no change" }
            $driverPath = Join-Path $dir "ci-installer-acceptance.mutant.ps1"
            Set-Content -LiteralPath $driverPath -Value $mut
        }
        $threw = $null
        try {
            & {
                $acceptance = $dir; $accounts = "a.json"; $logRoot = $dir; $root = $dir
                $serverLow = "l.exe"; $serverHigh = "h.exe"; $serverHigh2 = "h2.exe"; $serverHighHooks = "hh.exe"
                $kioskLow = "kl.exe"; $kioskHigh = "kh.exe"; $kioskHighHooks = "khh.exe"
                $lowVersion = "0.0.9"; $appVersion = "1.0.0"; $high2Version = "1.0.1"
                . $driverPath
            } *>&1 | Out-Null
        } catch { $threw = $_.Exception.Message }
        if ($ExpectThrow -and -not $threw) { throw "ledger self-check ${Label}: driver must fail but passed" }
        if (-not $ExpectThrow -and $threw) { throw "ledger self-check ${Label}: driver must pass but threw: $threw" }
        if ($ExpectThrow -and $ExpectMessage -and ($threw -notlike $ExpectMessage)) {
            throw "ledger self-check ${Label}: driver failed for the wrong reason; expected [$ExpectMessage] got [$threw]"
        }
        Write-Host "ledger self-check: $Label ok$(if ($threw) { " (threw: $threw)" })"
    } finally {
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$p = "param([string]`$Mode, [switch]`$SkipDowngradeCheck, `$LowSetup, `$HighSetup, `$AccountsFile, `$LowVersion, `$HighVersion, `$LogsDir, `$ServerSetup, `$KioskSetup, `$RepoRoot, `$LowKioskSetup, `$HighKioskSetup, `$ServerSetupHooks, `$KioskSetupHooks)`n"
Invoke-LedgerCase -Label "all legs ok" -ExpectThrow $false
Invoke-LedgerCase -Label "allow-listed phase-matrix skips" -ExpectThrow $false -Override @{
    "Invoke-R8f-PostinstallPhaseMatrix.ps1" = $p + "Write-Host 'R8f-phase-matrix fresh: skip webview2 (WebView2 runtime already installed on this runner)'`nWrite-Host 'R8f-phase-matrix fresh: skip reinstall-kept-pg (needs kept pgdata; not reachable from a reset machine)'`nWrite-Host 'R8f-phase-matrix: ok'"
}
Invoke-LedgerCase -Label "leg without ok marker" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B8 finished without its ok marker*" -Override @{ "Invoke-B8-WindowsBillingRestart.ps1" = $p + "Write-Host 'B-8: nothing to do'" }
Invoke-LedgerCase -Label "B-3 with zero dumps" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B3-BackupNeverDeletes finished without its ok marker*" -Override @{ "Invoke-B3-BackupNeverDeletes.ps1" = $p + "Write-Host 'B-3: ok dumps=0'" }
Invoke-LedgerCase -Label "unlisted skip" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B1 printed a skip that is not allow-listed*" -Override @{ "Invoke-B1-CleanServerInstall.ps1" = $p + "Write-Host 'B-1: shell exe not in payload (server-only); skip shell ACL pin'`nWrite-Host 'B-1: ok'" }
Invoke-LedgerCase -Label "throwing leg" -ExpectThrow $true -ExpectMessage "Q5 boom" -Override @{ "Invoke-Q5-SilentOpenSessions.ps1" = $p + "throw 'Q5 boom'" }
Invoke-LedgerCase -Label "Write-Error leg" -ExpectThrow $true -ExpectMessage "*B-4 boom*" -Override @{ "Invoke-B4-UninstallReinstall.ps1" = $p + "`$ErrorActionPreference = 'Stop'; Write-Error 'B-4 boom'; Write-Host 'B-4: ok backup=x'" }
# R10f item 7 (rr4 L-1): every case the review found uncaught.
# A real native process exiting 3 (cmd.exe on the Windows runner; sh where the self-check runs on Linux pwsh).
$native3 = "if (`$IsLinux -or `$IsMacOS) { & sh -c 'exit 3' } else { & cmd.exe /c 'exit 3' }`n"
Invoke-LedgerCase -Label "skipped wording" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B1 printed a skip that is not allow-listed*" -Override @{ "Invoke-B1-CleanServerInstall.ps1" = $p + "Write-Host 'B-1: shell ACL pin skipped (no admin)'`nWrite-Host 'B-1: ok'" }
Invoke-LedgerCase -Label "dash skip wording" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B5 printed a skip that is not allow-listed*" -Override @{ "Invoke-B5-KioskInstall.ps1" = $p + "Write-Host '- skip kiosk ACL probe'`nWrite-Host 'B-5: ok length=1'" }
Invoke-LedgerCase -Label "not run wording" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B7 printed a skip that is not allow-listed*" -Override @{ "Invoke-B7-KioskUpgrade.ps1" = $p + "Write-Host 'B-7: guard restart check not run'`nWrite-Host 'B-7: ok serverBase=http://x'" }
Invoke-LedgerCase -Label "skipped test in a VSTest summary" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B6-B12 printed a skip*" -Override @{ "Invoke-B6-B12-KioskFirstRun.ps1" = $p + "Write-Host 'Passed!  - Failed:     0, Passed:     4, Skipped:     1, Total:     5'`nWrite-Host 'B-6 + B-12: ok'" }
Invoke-LedgerCase -Label "VSTest summary with Skipped: 0 is not a skip" -ExpectThrow $false -Override @{ "Invoke-B6-B12-KioskFirstRun.ps1" = $p + "Write-Host 'Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 1 s'`nWrite-Host 'B-6 + B-12: ok'" }
Invoke-LedgerCase -Label "allow-listed skip printed by another leg" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG R8f-PostinstallFail printed a skip that is not allow-listed for this leg*" -Override @{ "Invoke-R8f-PostinstallFail.ps1" = $p + "Write-Host 'R8f-phase-matrix fresh: skip webview2 (WebView2 runtime already installed on this runner)'`nWrite-Host 'R8f-1: ok'" }
Invoke-LedgerCase -Label "exit 1 after the ok marker" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B4 left a non-zero exit code 1*" -Override @{ "Invoke-B4-UninstallReinstall.ps1" = $p + "Write-Host 'B-4: ok backup=x'`nexit 1" }
Invoke-LedgerCase -Label "native non-zero exit after the ok marker" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B13 left a non-zero exit code 3*" -Override @{ "Invoke-B13-DefaultPinUpgrade.ps1" = $p + "Write-Host 'B-13: ok'`n" + $native3 }
Invoke-LedgerCase -Label "unchecked native non-zero exit before the ok marker" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B3-BackupFlags left a non-zero exit code 3*" -Override @{ "Invoke-B3-BackupFlags.ps1" = $p + $native3 + "Write-Host 'B-3-flags: ok'" }
Invoke-LedgerCase -Label "expected native failure checked and consumed" -ExpectThrow $false -Override @{ "Reset-InstallerAcceptanceMachine.ps1" = $p + $native3 + "if (`$LASTEXITCODE -ne 3) { throw 'x' }`n`$global:LASTEXITCODE = 0`nWrite-Host 'Reset-InstallerAcceptanceMachine: ok'" }
Invoke-LedgerCase -Label "leg removed from the one leg table (21 legs)" -ExpectThrow $true -ExpectMessage "ci-acceptance: leg table has 21 legs*pinned count is 22" -DriverTransform {
    param($src) $src -replace '(?m)^\s*@\{ Name = "B13";[^\r\n]*\r?\n', ''
}
Invoke-LedgerCase -Label "leg duplicated in the leg table (23 legs)" -ExpectThrow $true -ExpectMessage "ci-acceptance: leg table has 23 legs (duplicates=``[B13``])*" -DriverTransform {
    param($src) $src -replace '(?m)^(\s*@\{ Name = "B13";[^\r\n]*\r?\n)', '$1$1'
}
Write-Host "ledger self-check: ok"
