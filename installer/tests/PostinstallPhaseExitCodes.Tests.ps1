$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $root "installer/ci/checkpoint-setup-exit-codes.ps1")

$freshPhases = @(
    "webview2", "reinstall-kept-pg", "database", "accounts", "services", "firewall", "backup", "start"
)
$upgradePhases = @(
    "upgrade-pins", "upgrade-stop", "upgrade-backup", "upgrade-start"
)
$allPhases = $freshPhases + $upgradePhases

$runIss = Get-Content -LiteralPath (Join-Path $root "installer/server/ServerPostinstallRun.iss") -Raw
$wizard = Get-Content -LiteralPath (Join-Path $root "installer/server/ServerWizard.inc.iss") -Raw
$kioskWizard = Get-Content -LiteralPath (Join-Path $root "installer/kiosk/KioskWizard.inc.iss") -Raw
$docs = Get-Content -LiteralPath (Join-Path $root "docs/install/server-install.fa.md") -Raw
$kioskDocs = Get-Content -LiteralPath (Join-Path $root "docs/install/kiosk-install.fa.md") -Raw
$readme = Get-Content -LiteralPath (Join-Path $root "installer/README.md") -Raw

$seenCodes = @{}
foreach ($phase in $allPhases) {
    if ($runIss -notmatch "RunServerPostinstallExec\('$([regex]::Escape($phase))'") {
        throw "PostinstallPhaseExitCodes: ServerPostinstallRun missing phase $phase"
    }
    $code = Get-CheckpointPostinstallPhaseExitCode -InstallPhase $phase
    if ($code -lt 133 -or $code -gt 144) {
        throw "PostinstallPhaseExitCodes: phase $phase code $code out of range 133-144"
    }
    if ($seenCodes.ContainsKey($code)) {
        throw "PostinstallPhaseExitCodes: duplicate exit $code for phases $($seenCodes[$code]) and $phase"
    }
    $seenCodes[$code] = $phase
    if ($docs -notmatch "\|\s*$code\s*\|") {
        throw "PostinstallPhaseExitCodes: server-install.fa.md missing row for code $code ($phase)"
    }
    if ($readme -notmatch "\|\s*$code\s*\|") {
        throw "PostinstallPhaseExitCodes: installer README missing row for code $code ($phase)"
    }
}

# .iss side of the table: constant values and the FailExit passed per phase must match the PS table,
# and every phase call must be gated "if not RunServerPostinstallExec(...) then Exit;".
$issConst = @{}
foreach ($m in [regex]::Matches($wizard, '(?m)^\s*(CHECKPOINT_EXIT_[A-Z0-9_]+)\s*=\s*(\d+)\s*;')) {
    $issConst[$m.Groups[1].Value] = [int]$m.Groups[2].Value
}
foreach ($phase in $allPhases) {
    $m = [regex]::Match($runIss, "(?m)^(.*)RunServerPostinstallExec\('$([regex]::Escape($phase))',[^;]*?,\s*(CHECKPOINT_EXIT_[A-Z0-9_]+)\)(.*)$")
    if (-not $m.Success) { throw "PostinstallPhaseExitCodes: cannot parse RunServerPostinstallExec call for $phase" }
    $const = $m.Groups[2].Value
    if (-not $issConst.ContainsKey($const)) { throw "PostinstallPhaseExitCodes: $const (phase $phase) not defined in ServerWizard.inc.iss" }
    $expected = Get-CheckpointPostinstallPhaseExitCode -InstallPhase $phase
    if ($issConst[$const] -ne $expected) {
        throw "PostinstallPhaseExitCodes: phase $phase passes $const=$($issConst[$const]) but table says $expected"
    }
    if ($m.Groups[1].Value -notmatch 'if not\s*$' -or $m.Groups[3].Value -notmatch '^\s*then Exit;') {
        throw "PostinstallPhaseExitCodes: phase $phase call is not gated with 'if not ... then Exit;'"
    }
}

if ($script:CheckpointSetupExitFailKioskPostinstall -ne 150) {
    throw "PostinstallPhaseExitCodes: kiosk postinstall code must be 150"
}
if ($kioskWizard -notmatch "CHECKPOINT_EXIT_FAIL_KIOSK_POSTINSTALL\s*=\s*150") {
    throw "PostinstallPhaseExitCodes: KioskWizard missing exit 150"
}
if ($kioskDocs -notmatch "\|\s*150\s*\|") {
    throw "PostinstallPhaseExitCodes: kiosk-install.fa.md missing code 150"
}
if ($readme -notmatch "\|\s*150\s*\|") {
    throw "PostinstallPhaseExitCodes: installer README missing kiosk code 150"
}

foreach ($reserved in @(128, 130, 131, 132)) {
    if ($wizard -notmatch "CHECKPOINT_EXIT_.*=\s*$reserved") {
        throw "PostinstallPhaseExitCodes: ServerWizard missing reserved code $reserved"
    }
}

Write-Host "PostinstallPhaseExitCodes: ok ($($allPhases.Count) server phases + kiosk 150)"