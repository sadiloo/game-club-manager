# Self-check for the leg ledger in ci-installer-acceptance.ps1 (R10e). Runs the real driver against fake leg scripts
# in a temp dir: all legs ok => passes; a leg without its ok marker, a non-allow-listed skip, or a failing leg => throws.
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
    param([string] $Label, [hashtable] $Override = @{}, [bool] $ExpectThrow, [string] $ExpectMessage = "")
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("cp-ledger-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    try {
        foreach ($k in $markers.Keys) {
            $body = if ($Override.ContainsKey($k)) { $Override[$k] } else { "param([string]`$Mode, [switch]`$SkipDowngradeCheck, `$LowSetup, `$HighSetup, `$AccountsFile, `$LowVersion, `$HighVersion, `$LogsDir, `$ServerSetup, `$KioskSetup, `$RepoRoot, `$LowKioskSetup, `$HighKioskSetup, `$ServerSetupHooks, `$KioskSetupHooks)`nWrite-Host `"$($markers[$k])`"" }
            Set-Content -LiteralPath (Join-Path $dir $k) -Value $body
        }
        $threw = $null
        try {
            & {
                $acceptance = $dir; $accounts = "a.json"; $logRoot = $dir; $root = $dir
                $serverLow = "l.exe"; $serverHigh = "h.exe"; $serverHigh2 = "h2.exe"; $serverHighHooks = "hh.exe"
                $kioskLow = "kl.exe"; $kioskHigh = "kh.exe"; $kioskHighHooks = "khh.exe"
                $lowVersion = "0.0.9"; $appVersion = "1.0.0"; $high2Version = "1.0.1"
                . $driver
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
Write-Host "ledger self-check: ok"
