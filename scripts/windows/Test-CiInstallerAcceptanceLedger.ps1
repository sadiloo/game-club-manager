# Self-check for the leg ledger in ci-installer-acceptance.ps1 (R10e, hardened in R10f item 7). Runs the real driver
# against fake leg scripts in a temp dir: all legs ok => passes; a leg without its ok marker, any skip wording that is not
# allow-listed for THAT leg, a non-zero exit (exit N, also after the ok marker, or an unchecked native exit code), a
# throwing leg, or a leg table whose names no longer match the pinned names sha256 => throws. The leg count is derived
# from the table (R10h); a self-check below fails if a literal count pin comes back.
# R10g: also a native failure hidden by a later native success, omitted / n/a wording, a 21/21 edit (leg dropped AND
# count line changed), and every failure must print "ci-acceptance: LEG FAIL <name> (exit N, phase X)".
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
    "Invoke-B7-KioskUpgrade.ps1"               = "B-7: ok serverBase=http://x completedAtUtc=2026-01-01T00:00:00Z preserved"
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
    param([string] $Label, [hashtable] $Override = @{}, [bool] $ExpectThrow, [string] $ExpectMessage = "", [scriptblock] $DriverTransform = $null,
        [string] $ExpectFailLine = "")
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
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot "AcceptanceNativeExitAudit.ps1") -Destination $dir
            Set-Content -LiteralPath $driverPath -Value $mut
        }
        $threw = $null
        $out = [System.Collections.Generic.List[string]]::new()
        try {
            & {
                $acceptance = $dir; $accounts = "a.json"; $logRoot = $dir; $root = $dir
                $serverLow = "l.exe"; $serverHigh = "h.exe"; $serverHigh2 = "h2.exe"; $serverHighHooks = "hh.exe"
                $kioskLow = "kl.exe"; $kioskHigh = "kh.exe"; $kioskHighHooks = "khh.exe"
                $lowVersion = "0.0.9"; $appVersion = "1.0.0"; $high2Version = "1.0.1"
                . $driverPath
            } *>&1 | ForEach-Object { $out.Add("$_") }
        } catch { $threw = $_.Exception.Message }
        if ($ExpectThrow -and -not $threw) { throw "ledger self-check ${Label}: driver must fail but passed" }
        if (-not $ExpectThrow -and $threw) { throw "ledger self-check ${Label}: driver must pass but threw: $threw" }
        if ($ExpectThrow -and $ExpectMessage -and ($threw -notlike $ExpectMessage)) {
            throw "ledger self-check ${Label}: driver failed for the wrong reason; expected [$ExpectMessage] got [$threw]"
        }
        if ($ExpectFailLine) {
            $failLines = @($out | Where-Object { $_ -like "ci-acceptance: LEG FAIL *" })
            if ($failLines.Count -ne 1 -or $failLines[0] -ne $ExpectFailLine) {
                throw "ledger self-check ${Label}: expected exactly the line [$ExpectFailLine], got [$($failLines -join ' | ')]"
            }
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
# R10g: ONE native call site (the native-exit audit counts call sites), so a case can add exactly one check or annotation.
$nativeSetup = "`$nat = if (`$IsLinux -or `$IsMacOS) { @('sh', '-c') } else { @('cmd.exe', '/c') }`n"
$native3 = $nativeSetup + "& `$nat[0] `$nat[1] 'exit 3'`n"
$native3Ignored = $nativeSetup + "& `$nat[0] `$nat[1] 'exit 3' # native-exit: ignored self-check leaves the exit behind on purpose`n"
Invoke-LedgerCase -Label "skipped wording" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B1 printed a skip that is not allow-listed*" -Override @{ "Invoke-B1-CleanServerInstall.ps1" = $p + "Write-Host 'B-1: shell ACL pin skipped (no admin)'`nWrite-Host 'B-1: ok'" }
Invoke-LedgerCase -Label "dash skip wording" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B5 printed a skip that is not allow-listed*" -Override @{ "Invoke-B5-KioskInstall.ps1" = $p + "Write-Host '- skip kiosk ACL probe'`nWrite-Host 'B-5: ok length=1'" }
Invoke-LedgerCase -Label "not run wording" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B7 printed a skip that is not allow-listed*" -Override @{ "Invoke-B7-KioskUpgrade.ps1" = $p + "Write-Host 'B-7: guard restart check not run'`nWrite-Host 'B-7: ok serverBase=http://x completedAtUtc=2026-01-01T00:00:00Z preserved'" }
# R10i GO bar: B7 counts only with the full "B-7: ok serverBase=... completedAtUtc=... preserved" marker.
Invoke-LedgerCase -Label "B7 ok line without preserved" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B7 finished without its ok marker*preserved*" -Override @{ "Invoke-B7-KioskUpgrade.ps1" = $p + "Write-Host 'B-7: ok serverBase=http://x'" }
Invoke-LedgerCase -Label "skipped test in a VSTest summary" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B6-B12 printed a skip*" -Override @{ "Invoke-B6-B12-KioskFirstRun.ps1" = $p + "Write-Host 'Passed!  - Failed:     0, Passed:     4, Skipped:     1, Total:     5'`nWrite-Host 'B-6 + B-12: ok'" }
Invoke-LedgerCase -Label "VSTest summary with Skipped: 0 is not a skip" -ExpectThrow $false -Override @{ "Invoke-B6-B12-KioskFirstRun.ps1" = $p + "Write-Host 'Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 1 s'`nWrite-Host 'B-6 + B-12: ok'" }
Invoke-LedgerCase -Label "allow-listed skip printed by another leg" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG R8f-PostinstallFail printed a skip that is not allow-listed for this leg*" -Override @{ "Invoke-R8f-PostinstallFail.ps1" = $p + "Write-Host 'R8f-phase-matrix fresh: skip webview2 (WebView2 runtime already installed on this runner)'`nWrite-Host 'R8f-1: ok'" }
Invoke-LedgerCase -Label "exit 1 after the ok marker" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B4 left a non-zero exit code 1*" -Override @{ "Invoke-B4-UninstallReinstall.ps1" = $p + "Write-Host 'B-4: ok backup=x'`nexit 1" }
Invoke-LedgerCase -Label "native non-zero exit after the ok marker" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B13 left a non-zero exit code 3*" -ExpectFailLine "ci-acceptance: LEG FAIL B13 (exit 3, phase exit-code)" -Override @{ "Invoke-B13-DefaultPinUpgrade.ps1" = $p + "Write-Host 'B-13: ok'`n" + $native3Ignored }
Invoke-LedgerCase -Label "unchecked native non-zero exit before the ok marker" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B3-BackupFlags left a non-zero exit code 3*" -Override @{ "Invoke-B3-BackupFlags.ps1" = $p + $native3Ignored + "Write-Host 'B-3-flags: ok'" }
Invoke-LedgerCase -Label "unchecked native call (no check, no annotation)" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B3-BackupFlags has native calls whose exit code is never checked*Invoke-B3-BackupFlags.ps1:3:*" -ExpectFailLine "ci-acceptance: LEG FAIL B3-BackupFlags (exit 0, phase native-audit)" -Override @{ "Invoke-B3-BackupFlags.ps1" = $p + $native3 + "Write-Host 'B-3-flags: ok'" }
Invoke-LedgerCase -Label "expected native failure checked and consumed" -ExpectThrow $false -Override @{ "Reset-InstallerAcceptanceMachine.ps1" = $p + $native3 + "if (`$LASTEXITCODE -ne 3) { throw 'x' }`n`$global:LASTEXITCODE = 0`nWrite-Host 'Reset-InstallerAcceptanceMachine: ok'" }
# R10g item 4 (rr5 L11b): a native failure followed by a native success. $LASTEXITCODE ends at 0, the leg must still fail.
Invoke-LedgerCase -Label "native failure followed by a native success" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B7 has native calls whose exit code is never checked*Invoke-B7-KioskUpgrade.ps1:3:*" -ExpectFailLine "ci-acceptance: LEG FAIL B7 (exit 0, phase native-audit)" -Override @{
    "Invoke-B7-KioskUpgrade.ps1" = $p + $nativeSetup + "& `$nat[0] `$nat[1] 'exit 3'`n& `$nat[0] `$nat[1] 'exit 0'`nif (`$LASTEXITCODE -ne 0) { throw 'x' }`nWrite-Host 'B-7: ok serverBase=http://x completedAtUtc=2026-01-01T00:00:00Z preserved'"
}
Invoke-LedgerCase -Label "native failure blindly reset (assignment is not a check), then a native success" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B7 has native calls whose exit code is never checked*Invoke-B7-KioskUpgrade.ps1:3:*" -ExpectFailLine "ci-acceptance: LEG FAIL B7 (exit 0, phase native-audit)" -Override @{
    "Invoke-B7-KioskUpgrade.ps1" = $p + $nativeSetup + "& `$nat[0] `$nat[1] 'exit 3'`n`$global:LASTEXITCODE = 0`n& `$nat[0] `$nat[1] 'exit 0'`nif (`$LASTEXITCODE -ne 0) { throw 'x' }`nWrite-Host 'B-7: ok serverBase=http://x completedAtUtc=2026-01-01T00:00:00Z preserved'"
}
Invoke-LedgerCase -Label "native failure checked, then a native success" -ExpectThrow $false -Override @{
    "Invoke-B7-KioskUpgrade.ps1" = $p + $nativeSetup + "& `$nat[0] `$nat[1] 'exit 3'`nif (`$LASTEXITCODE -ne 3) { throw 'x' }`n& `$nat[0] `$nat[1] 'exit 0'`nif (`$LASTEXITCODE -ne 0) { throw 'x' }`nWrite-Host 'B-7: ok serverBase=http://x completedAtUtc=2026-01-01T00:00:00Z preserved'"
}
# R10g item 4 (rr5 L17): omitted / n/a / not applicable wording.
Invoke-LedgerCase -Label "omitted (n/a) wording" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B8 printed a skip that is not allow-listed*" -ExpectFailLine "ci-acceptance: LEG FAIL B8 (exit 0, phase skip)" -Override @{ "Invoke-B8-WindowsBillingRestart.ps1" = $p + "Write-Host 'B-8: pc billing check omitted'`nWrite-Host 'B-8: ok ps5=1 pc=1 cap=1'" }
Invoke-LedgerCase -Label "n/a wording" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B5 printed a skip that is not allow-listed*" -Override @{ "Invoke-B5-KioskInstall.ps1" = $p + "Write-Host 'B-5: ACL probe: n/a'`nWrite-Host 'B-5: ok length=1'" }
Invoke-LedgerCase -Label "not applicable wording" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B5 printed a skip that is not allow-listed*" -Override @{ "Invoke-B5-KioskInstall.ps1" = $p + "Write-Host 'B-5: ACL probe not applicable here'`nWrite-Host 'B-5: ok length=1'" }
# R10g item 3: LEG FAIL line for a throwing leg and for a missing ok marker.
Invoke-LedgerCase -Label "LEG FAIL line for a throwing leg" -ExpectThrow $true -ExpectMessage "Q5 boom" -ExpectFailLine "ci-acceptance: LEG FAIL Q5-SilentOpenSessions (exit 0, phase run)" -Override @{ "Invoke-Q5-SilentOpenSessions.ps1" = $p + "throw 'Q5 boom'" }
Invoke-LedgerCase -Label "LEG FAIL line for a missing ok marker" -ExpectThrow $true -ExpectMessage "ci-acceptance: LEG B1 finished without its ok marker*" -ExpectFailLine "ci-acceptance: LEG FAIL B1 (exit 0, phase ok-marker)" -Override @{ "Invoke-B1-CleanServerInstall.ps1" = $p + "Write-Host 'B-1: nothing'" }
Invoke-LedgerCase -Label "LEG FAIL line for exit 4 thrown after a failed native" -ExpectThrow $true -ExpectMessage "boom4" -ExpectFailLine "ci-acceptance: LEG FAIL B4 (exit 4, phase run)" -Override @{ "Invoke-B4-UninstallReinstall.ps1" = $p + $nativeSetup + "& `$nat[0] `$nat[1] 'exit 4'`nif (`$LASTEXITCODE -ne 0) { throw 'boom4' }" }
Invoke-LedgerCase -Label "leg removed from the one leg table (21 legs)" -ExpectThrow $true -ExpectMessage "ci-acceptance: leg names sha256 * does not match the pinned * (leg table has 21 legs:*" -DriverTransform {
    param($src) $src -replace '(?m)^\s*@\{ Name = "B13";[^\r\n]*\r?\n', ''
}
Invoke-LedgerCase -Label "leg duplicated in the leg table (23 legs)" -ExpectThrow $true -ExpectMessage "ci-acceptance: leg table has 23 legs with duplicates=``[B13``]*" -DriverTransform {
    param($src) $src -replace '(?m)^(\s*@\{ Name = "B13";[^\r\n]*\r?\n)', '$1$1'
}
# R10h: dropping a different leg fails the same single pin (there is no count line left to edit).
Invoke-LedgerCase -Label "leg dropped (R8f-InvalidAccounts)" -ExpectThrow $true -ExpectMessage "ci-acceptance: leg names sha256 * does not match the pinned *" -DriverTransform {
    param($src) $src -replace '(?m)^\s*@\{ Name = "R8f-InvalidAccounts";[^\r\n]*\r?\n', ''
}
# ... and with the sha pin edited too, the unused leg script still fails it.
Invoke-LedgerCase -Label "leg dropped with the sha pin recomputed: leg script unused" -ExpectThrow $true -ExpectMessage "ci-acceptance: acceptance scripts not used by any leg: Invoke-R8f-InvalidAccounts.ps1" -DriverTransform {
    param($src)
    $m = $src -replace '(?m)^\s*@\{ Name = "R8f-InvalidAccounts";[^\r\n]*\r?\n', ''
    $names = [regex]::Matches($m, '(?m)^\s*@\{ Name = "([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
    $sha = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($names -join "`n"))).ToLowerInvariant()
    $m -replace 'AcceptanceLegNamesSha256 = "[0-9a-f]{64}"', ('AcceptanceLegNamesSha256 = "' + $sha + '"')
}
# R10g: the REAL acceptance scripts pass the native-exit audit (the driver also refuses them per leg on Windows), and a
# real check removed again (B-1 icacls) is found.
. (Join-Path $PSScriptRoot "AcceptanceNativeExitAudit.ps1")
$realAcceptance = Join-Path $PSScriptRoot "..\..\installer\tools\acceptance"
$realFindings = @(Get-ChildItem -LiteralPath $realAcceptance -Filter "*.ps1" -File | ForEach-Object { Get-AcceptanceUncheckedNativeCalls $_.FullName })
if ($realFindings.Count -ne 0) { throw "ledger self-check: real acceptance scripts have unchecked native calls: $($realFindings -join ' | ')" }
$b1 = Get-Content -LiteralPath (Join-Path $realAcceptance "Invoke-B1-CleanServerInstall.ps1") -Raw
$b1Mut = $b1 -replace '(?m)^if \(\$LASTEXITCODE -ne 0\) \{ throw "B-1: icacls[^\r\n]*\r?\n', ''
if ($b1Mut -eq $b1) { throw "ledger self-check: B-1 icacls check not found for the audit negative control" }
$tmpB1 = Join-Path ([IO.Path]::GetTempPath()) ("cp-audit-" + [guid]::NewGuid().ToString("N").Substring(0, 8) + ".ps1")
try {
    Set-Content -LiteralPath $tmpB1 -Value $b1Mut
    $neg = @(Get-AcceptanceUncheckedNativeCalls $tmpB1)
    if ($neg.Count -ne 1 -or $neg[0] -notmatch 'icacls') { throw "ledger self-check: audit missed the unchecked icacls: [$($neg -join ' | ')]" }
} finally { Remove-Item -LiteralPath $tmpB1 -Force -ErrorAction SilentlyContinue }
Write-Host "ledger self-check: real acceptance scripts native-exit audit ok (negative control icacls found)"
# R10h (rr6 report honesty): the leg count is derived, never pinned. The driver must not contain a literal count pin,
# it must derive the count from the table, and every count it prints must be that derived value. Negative control: a
# literal pin put back is found.
function Get-LegCountPinProblems {
    param([string] $Src)
    $p = [System.Collections.Generic.List[string]]::new()
    if ($Src -match '(?m)^\s*\$script:AcceptanceExpectedLegCount\s*=\s*\d') { $p.Add("literal leg-count pin present") }
    if (([regex]::Matches($Src, '(?m)^\s*\$script:AcceptanceExpectedLegCount\s*=')).Count -ne 1 -or $Src -notmatch '(?m)^\$script:AcceptanceExpectedLegCount = \$expectedLegs\.Count\r?$') { $p.Add("count must be assigned once, from `$expectedLegs.Count") }
    if ($Src -match '(?i)pinned count') { $p.Add("'pinned count' wording left") }
    return $p.ToArray()
}
$driverSrc = Get-Content -LiteralPath $driver -Raw
$pinProblems = @(Get-LegCountPinProblems $driverSrc)
if ($pinProblems.Count -ne 0) { throw "ledger self-check: leg count: $($pinProblems -join ' | ')" }
$tableRows = ([regex]::Matches($driverSrc, '(?m)^\s*@\{ Name = "')).Count
if ($tableRows -lt 1) { throw "ledger self-check: leg table rows not found" }
# Every printed count (table line, ledger mismatch, LEDGER n/m) is the derived variable, not a number.
foreach ($line in @([regex]::Matches($driverSrc, '(?m)^.*(ci-acceptance: leg table|ledger mismatch ran=|ci-acceptance: LEDGER ).*$') | ForEach-Object { $_.Value })) {
    if ($line -match '/\d|\b\d+ legs') { throw "ledger self-check: a printed leg count is a literal: $line" }
}
$pinnedBack = $driverSrc -replace '(?m)^\$script:AcceptanceExpectedLegCount = \$expectedLegs\.Count', ('$script:AcceptanceExpectedLegCount = ' + $tableRows)
if ($pinnedBack -eq $driverSrc -or @(Get-LegCountPinProblems $pinnedBack).Count -eq 0) { throw "ledger self-check: negative control (literal count put back) not found" }
Write-Host "ledger self-check: leg count derived from the table ($tableRows rows), no literal pin (negative control found)"
# R10h static re-walk pins (legs B7..Q5 have not run on CI yet): the phase matrix must pass a pins file that exists, or
# Setup never runs upgrade-pins; B-7 must do a real low -> high upgrade with logs and keep the seeded config.
$pm = Get-Content -LiteralPath (Join-Path $realAcceptance "Invoke-R8f-PostinstallPhaseMatrix.ps1") -Raw
$pmLine = [regex]::Match($pm, '(?m)^\$defaultPins = Join-Path \$PSScriptRoot "([^"]+)"\r?$')
if (-not $pmLine.Success) { throw "ledger self-check: phase matrix default pins path line not found" }
$pmPath = Join-Path $realAcceptance $pmLine.Groups[1].Value
if (-not (Test-Path -LiteralPath $pmPath)) { throw "ledger self-check: phase matrix default pins file does not exist: $pmPath" }
if ($pm -notmatch '(?m)^if \(-not \(Test-Path -LiteralPath \$defaultPins\)\) \{ throw ') { throw "ledger self-check: phase matrix must throw when the pins file is missing" }
$b7 = Get-Content -LiteralPath (Join-Path $realAcceptance "Invoke-B7-KioskUpgrade.ps1") -Raw
foreach ($pin in @('-FilePath $LowKioskSetup -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=$lowLog")',
        '-FilePath $HighKioskSetup -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=$highLog")',
        'Assert-KioskPostinstallCompleted -InnoLogPath $highLog',
        'throw "B-7: setup.json serverBase missing', 'throw "B-7: upgrade changed serverBase', 'throw "B-7: upgrade changed completedAtUtc')) {
    if ($b7.IndexOf($pin, [StringComparison]::Ordinal) -lt 0) { throw "ledger self-check: B-7 lost: $pin" }
}
# The seed condition itself (its own line, not the later "seeding failed" check with the same test).
if ($b7 -notmatch '(?m)^if \(\$before\.State -ne "configured"\) \{\r?$') { throw "ledger self-check: B-7 must seed unless setup.json is already configured" }
# R10i OBS-1 (ruling): completedAtUtc is compared as raw JSON text (ordinal), never via a parsed DateTime string.
foreach ($pin in @('[System.Text.Json.JsonDocument]::Parse($Raw)', '$beforeCompleted = Get-B7RawJsonProperty -Raw $before.Raw -Name "completedAtUtc"',
        '$afterCompleted = Get-B7RawJsonProperty -Raw $after.Raw -Name "completedAtUtc"',
        '[string]::Equals($afterCompleted.Token, $beforeCompleted.Token, [StringComparison]::Ordinal)')) {
    if ($b7.IndexOf($pin, [StringComparison]::Ordinal) -lt 0) { throw "ledger self-check: B-7 raw completedAtUtc compare lost: $pin" }
}
if ($b7 -match '\[string\]\$\w+(\.Json)?\.completedAtUtc') { throw "ledger self-check: B-7 must not compare completedAtUtc via [string] of the parsed JSON" }
# The ok line B-7 prints must satisfy the driver's B7 OkPattern (which requires "... preserved").
$b7Ok = [regex]::Match($b7, '(?m)^Write-Host "(B-7: ok [^"]+)"\s*$')
if (-not $b7Ok.Success) { throw "ledger self-check: B-7 ok line not found" }
$b7OkSample = $b7Ok.Groups[1].Value -replace '\$\([^)]*\)', 'v'
$b7Pattern = [regex]::Match($driverSrc, "Name = `"B7`";[^}]*OkPattern = '([^']+)'").Groups[1].Value
if (-not $b7Pattern -or $b7Pattern -notmatch 'preserved\$$') { throw "ledger self-check: driver B7 OkPattern must end with preserved`$ (got '$b7Pattern')" }
if ($b7OkSample -notmatch $b7Pattern) { throw "ledger self-check: B-7 ok line '$b7OkSample' does not match the driver OkPattern /$b7Pattern/" }
# R10j (BL-rr8-1) item 1d: B-7 checks Guard Running IMMEDIATELY after each kiosk Setup returns (no wait, no retry
# helper), and item 1a: any B-7 failure dumps both Inno logs + Guard evidence before rethrowing.
$b7n = $b7 -replace "`r`n", "`n"
foreach ($pair in @(
        @('if ($pLow.ExitCode -notin 0, 3010) { throw "B-7: low kiosk install exit $($pLow.ExitCode)" }', 'Assert-B7GuardRunningNow -When "low install"'),
        @('if ($p.ExitCode -notin 0, 3010) { throw "B-7: upgrade exit $($p.ExitCode)" }', 'Assert-B7GuardRunningNow -When "upgrade"'))) {
    if ($b7n.IndexOf($pair[0] + "`n" + $pair[1] + "`n", [StringComparison]::Ordinal) -lt 0) { throw "ledger self-check: B-7 must check Guard Running right after Setup returns: $($pair[1])" }
}
$b7Assert = [regex]::Match($b7n, '(?s)\nfunction Assert-B7GuardRunningNow \{\n(.*?)\n\}\n')
if (-not $b7Assert.Success -or $b7Assert.Groups[1].Value -notmatch [regex]::Escape('if ($guard.Status -ne "Running") {') -or
    $b7Assert.Groups[1].Value -notmatch [regex]::Escape('throw "B-7: CheckpointGuard must be Running after $When; status=$($guard.Status)"') -or
    $b7Assert.Groups[1].Value -match 'Start-Sleep|while|for \(|Start-Service|sc\.exe') {
    throw "ledger self-check: Assert-B7GuardRunningNow must be one immediate Running check (no wait / retry / start)"
}
if ($b7 -match 'Start-InstallerCheckpointGuard|Start-Service|Restart-Service|Start-Sleep') { throw "ledger self-check: B-7 must not start, restart or wait for CheckpointGuard (R10j item 1d)" }
if ($b7n -notmatch '(?m)^\} catch \{\n    Write-B7FailureDiagnostics -InnoLogs @\(\$lowLog, \$highLog\)\n    throw\n\}\nWrite-Host "B-7: ok ') { throw "ledger self-check: B-7 failures must dump diagnostics for both Inno logs and rethrow, right before the ok line" }
if ($b7n -notmatch '(?m)^try \{\n\$pLow = Start-Process ') { throw "ledger self-check: the B-7 diagnostics try must start before the low install" }
$gd = Get-Content -LiteralPath (Join-Path $realAcceptance "Get-CheckpointGuardDiagnostics.ps1") -Raw
foreach ($pin in @('foreach ($verb in @("query", "qc", "qfailure"))', '[System.IO.Path]::Combine($programData, "Checkpoint\guard\watchdog.log")', 'LogName = "System"; ProviderName = "Service Control Manager"', "GameClub\.Kiosk\.Guard")) {
    if ($gd.IndexOf($pin, [StringComparison]::Ordinal) -lt 0) { throw "ledger self-check: Guard diagnostics lost: $pin" }
}
Write-Host "ledger self-check: B-7 immediate Guard Running checks (low + upgrade) and failure diagnostics pinned"
Write-Host "ledger self-check: phase-matrix pins path and B-7 upgrade/seed pins ok"
Write-Host "ledger self-check: ok"
