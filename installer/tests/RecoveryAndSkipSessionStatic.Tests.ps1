# R10e static guards (CI side; behaviour is pinned by scripts/installer-wine legs):
#   PO-F   the LEFT_STOPPED lines (Inno + PowerShell) print the exact recovery command, incl. /SKIPSESSIONCHECK=1;
#   item 12 /SKIPSESSIONCHECK is read ONLY through SkipSessionCheckRequested, which logs every use;
#   item 7 a failed FIRST install stops CheckpointServer and CheckpointPostgres (Inno side);
#   DEF-13b the accounts phase refuses a config without a Manager / with a non-name role;
#   docs  installer/README.md and the fa server-install doc describe recovery and /SKIPSESSIONCHECK.
$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$errors = @()

$inc = Get-Content -LiteralPath (Join-Path $root "installer/server/ServerWizard.inc.iss") -Raw
$run = Get-Content -LiteralPath (Join-Path $root "installer/server/ServerPostinstallRun.iss") -Raw
$ps = Get-Content -LiteralPath (Join-Path $root "installer/tools/server-postinstall.ps1") -Raw

# PO-F recovery command
$recovery = '/SKIPSESSIONCHECK=1 /LOG='
$m = [regex]::Match($inc, '(?s)function CheckpointRecoveryCommandLine: String;.*?\r?\nend;')
if (-not $m.Success) { $errors += "Inno: CheckpointRecoveryCommandLine missing" }
elseif ($m.Value -notmatch [regex]::Escape("ExpandConstant('{srcexe}')") -or $m.Value -notmatch [regex]::Escape($recovery) -or $m.Value -notmatch 'setup-recovery\.log') {
    $errors += "Inno: recovery command must be `"{srcexe}`" /SKIPSESSIONCHECK=1 /LOG=...setup-recovery.log"
}
$m = [regex]::Match($inc, '(?s)procedure RestoreCheckpointUpgradeAfterPostinstallFailure;.*?\r?\nend;')
if (-not $m.Success -or $m.Value -notmatch "Log\('CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED recovery_command=' \+ CheckpointRecoveryCommandLine\);\s*\r?\n\s*Exit;") {
    $errors += "Inno: the LEFT_STOPPED branch must log recovery_command= right before Exit"
}
if ($run -notmatch [regex]::Escape("' -SetupExe `"' + ExpandConstant('{srcexe}') + '`"'")) { $errors += "Inno: RunServerPostinstallExec must pass -SetupExe {srcexe}" }
if ($ps -notmatch [regex]::Escape('Write-Host "CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED recovery_command=$(Get-CheckpointRecoveryCommandLine)"')) {
    $errors += "PowerShell: LEFT_STOPPED must print recovery_command="
}
if ($ps -notmatch [regex]::Escape('/SKIPSESSIONCHECK=1 /LOG=`"C:\ProgramData\Checkpoint\logs\setup-recovery.log`"')) { $errors += "PowerShell: recovery command text drifted from Inno" }

# item 12: SKIPSESSIONCHECK only via the logging function
$reads = [regex]::Matches($inc, [regex]::Escape("{param:SKIPSESSIONCHECK|}")).Count
if ($reads -ne 1) { $errors += "Inno: {param:SKIPSESSIONCHECK|} must be read exactly once (in SkipSessionCheckRequested); found $reads" }
$m = [regex]::Match($inc, '(?s)function SkipSessionCheckRequested\(const Context: String\): Boolean;.*?\r?\nend;')
if (-not $m.Success -or $m.Value -notmatch "if Result then\s*\r?\n\s*Log\('CHECKPOINT_SKIPSESSIONCHECK used=1") { $errors += "Inno: SkipSessionCheckRequested must log CHECKPOINT_SKIPSESSIONCHECK used=1 when the flag is present" }
# R10f item 8: only the value 1 skips; any other value is logged as ignored.
if (-not $m.Success -or $m.Value -notmatch [regex]::Escape("Result := V = '1';")) { $errors += "Inno: SkipSessionCheckRequested must skip only for /SKIPSESSIONCHECK=1" }
if (-not $m.Success -or $m.Value -notmatch "CHECKPOINT_SKIPSESSIONCHECK used=0 value=") { $errors += "Inno: an ignored /SKIPSESSIONCHECK value must be logged (used=0)" }
# R10f item 4: the silent 131 server-unreachable line prints the recovery command.
if ($inc -notmatch [regex]::Escape("Log('CHECKPOINT_OPEN_SESSIONS_SILENT reason=open_session_count_unknown_server_unreachable recovery_command=' +") + '\s*\r?\n\s*CheckpointRecoveryCommandLine\);') {
    $errors += "Inno: the silent 131 unreachable line must print recovery_command="
}
# DEF-21: the Inno failure line falls back to the phase AbortPostinstallPhaseFailed was given.
if ($inc -notmatch [regex]::Escape("  GSetupFailurePhase := Phase;") -or $inc -notmatch [regex]::Escape("PhaseOut := GSetupFailurePhase")) {
    $errors += "Inno: CHECKPOINT_POSTINSTALL_FAILED must name the real phase when no failure ini exists (DEF-21)"
}
foreach ($ctx in @("silent-upgrade", "interactive-upgrade")) {
    if ($inc -notmatch [regex]::Escape("if SkipSessionCheckRequested('$ctx') then Exit;")) { $errors += "Inno: $ctx open-session check must go through SkipSessionCheckRequested" }
}

# item 7: fresh-install failure stops both services
$m = [regex]::Match($inc, '(?s)procedure StopCheckpointServicesAfterFreshInstallFailure;.*?\r?\nend;')
if (-not $m.Success) { $errors += "Inno: StopCheckpointServicesAfterFreshInstallFailure missing" }
else {
    foreach ($s in @("'stop CheckpointServer'", "'stop CheckpointPostgres'", "if not IsFirstInstall then Exit;", "CHECKPOINT_SETUP_FRESH_ABORT_SERVICES_STOPPED")) {
        if ($m.Value -notmatch [regex]::Escape($s)) { $errors += "Inno: fresh-failure stop must contain $s" }
    }
}
$m = [regex]::Match($inc, '(?s)procedure AbortPostinstallPhaseFailed\(const Phase: String; const ExitCode: Integer\);.*?\r?\nend;')
if (-not $m.Success -or $m.Value.IndexOf('StopCheckpointServicesAfterFreshInstallFailure;') -lt 0 -or
    $m.Value.IndexOf('StopCheckpointServicesAfterFreshInstallFailure;') -gt $m.Value.IndexOf('ExitProcess(ExitCode);')) {
    $errors += "Inno: AbortPostinstallPhaseFailed must stop services before ExitProcess"
}
if ($ps -notmatch '(?s)CHECKPOINT_POSTINSTALL_ABORT_SERVICES_STOPPED"\s*\r?\n\s*Stop-Service CheckpointServer[^\r\n]*\r?\n\s*Stop-Service CheckpointPostgres') {
    $errors += "PowerShell: fresh abort cleanup must stop CheckpointServer and CheckpointPostgres"
}

# DEF-13b accounts phase
foreach ($s in @('CHECKPOINT_ACCOUNTS_INVALID at least one Manager is required', 'CHECKPOINT_ACCOUNTS_INVALID role must be a role name', 'CHECKPOINT_ACCOUNTS_INVALID blank pin hash')) {
    if ($ps -notmatch [regex]::Escape($s)) { $errors += "PowerShell accounts phase: missing '$s'" }
}

# docs
$readme = Get-Content -LiteralPath (Join-Path $root "installer/README.md") -Raw
$fa = Get-Content -LiteralPath (Join-Path $root "docs/install/server-install.fa.md") -Raw
foreach ($pair in @(@("installer/README.md", $readme), @("docs/install/server-install.fa.md", $fa))) {
    foreach ($s in @("/SKIPSESSIONCHECK=1", "CHECKPOINT_SKIPSESSIONCHECK", "recovery_command=", "setup-recovery.log", "restore.ps1")) {
        if ($pair[1] -notmatch [regex]::Escape($s)) { $errors += "$($pair[0]): recovery section must mention $s" }
    }
    # R10g item 7: /SKIPSESSIONCHECK doc pins (only =1 skips and is logged; others logged as ignored; unknown count -> 131).
    # Checked inside the /SKIPSESSIONCHECK section itself (the exit-code table elsewhere also names the 131 reason).
    $skipSec = [regex]::Match($pair[1], '(?ms)^### [^\r\n]*/SKIPSESSIONCHECK=1[^\r\n]*\r?\n.*?(?=^### |^\S[^\r\n]*kiosk-install\.fa\.md|\z)')
    if (-not $skipSec.Success) { $errors += "$($pair[0]): /SKIPSESSIONCHECK section not found" }
    foreach ($s in @("CHECKPOINT_SKIPSESSIONCHECK used=1", "CHECKPOINT_SKIPSESSIONCHECK used=0", "note=ignored: only /SKIPSESSIONCHECK=1 skips the open-session check",
            "/SKIPSESSIONCHECK=0", "131", "reason=open_session_count_unknown_server_unreachable")) {
        if ($skipSec.Value -notmatch [regex]::Escape($s)) { $errors += "$($pair[0]): /SKIPSESSIONCHECK section must mention $s" }
    }
    # R10g item 5: recovery path 2 order. Server stopped and disabled, THEN restore.ps1, THEN the previous Setup.
    $p2 = [regex]::Match($pair[1], '(?ms)^2\. \*\*.*?(?=^\S|\z)')
    if (-not $p2.Success) { $errors += "$($pair[0]): recovery path 2 not found" }
    else {
        $t = $p2.Value
        $iDisable = $t.IndexOf("sc.exe config CheckpointServer start= disabled")
        $iStop = $t.IndexOf("sc.exe stop CheckpointServer")
        $iRestore = $t.IndexOf("restore.ps1")
        $iReinstall = $t.IndexOf("CHECKPOINT_REINSTALL_KEPT_PGDATA")
        if ($iDisable -lt 0 -or $iStop -lt 0 -or $iRestore -lt 0 -or $iReinstall -lt 0 -or -not ($iDisable -lt $iStop -and $iStop -lt $iRestore -and $iRestore -lt $iReinstall)) {
            $errors += "$($pair[0]): recovery path 2 must stop+disable CheckpointServer, then restore.ps1, then install the previous version (disable=$iDisable stop=$iStop restore=$iRestore reinstall=$iReinstall)"
        }
    }
}

if ($errors.Count -gt 0) {
    foreach ($e in $errors) { Write-Error "RecoveryAndSkipSessionStatic: $e" -ErrorAction Continue }
    throw "RecoveryAndSkipSessionStatic: $($errors.Count) rule(s) failed"
}
Write-Host "RecoveryAndSkipSessionStatic: ok"
