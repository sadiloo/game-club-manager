# Box (pwsh 7 on Linux) self-asserting harness for the PowerShell side of the F6 ruling and the hook trace (see README.md).
# Runs installer/tools/server-postinstall.ps1 with a PSDrive C: mapped to a temp root and Start/Stop-Service stubbed.
# Exits 1 if any assertion fails.
param([string] $RepoRoot = (Join-Path $PSScriptRoot "../.."))
$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
# Stage tools the way scripts/windows/ci-installer.ps1 does (installer/tools + installer/ci/checkpoint-setup-exit-codes.ps1).
$ToolsDir = Join-Path ([IO.Path]::GetTempPath()) ("cp-f6-tools-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null
Get-ChildItem -LiteralPath (Join-Path $RepoRoot "installer/tools") -File | Where-Object { $_.Extension -in ".ps1", ".psm1" } | Copy-Item -Destination $ToolsDir
Copy-Item -LiteralPath (Join-Path $RepoRoot "installer/ci/checkpoint-setup-exit-codes.ps1") -Destination $ToolsDir
$script:fails = 0; $script:passes = 0
function Assert-Leg([string] $Name, [bool] $Ok, [string] $Detail) {
    if ($Ok) { Write-Host "PASS $Name"; $script:passes++ } else { Write-Host "FAIL $Name :: $Detail"; $script:fails++ }
}

function Invoke-Leg {
    param([string[]] $PhaseArgs, [hashtable] $Env = @{}, [scriptblock] $Prepare = $null, [switch] $StartServiceThrows)
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("cp-f6-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    $cRoot = Join-Path $tmp "c"
    foreach ($d in @("ProgramData/Checkpoint/config", "ProgramData/Checkpoint/logs", "ProgramData/Checkpoint/backups")) {
        New-Item -ItemType Directory -Force -Path (Join-Path $cRoot $d) | Out-Null
    }
    $null = New-Item -ItemType SymbolicLink -Path (Join-Path $tmp 'C:') -Target $cRoot
    if ($Prepare) { & $Prepare $cRoot }
    $svcLog = Join-Path $tmp "svc.log"
    $startBody = if ($StartServiceThrows) { "Add-Content '$svcLog' ('START ' + `$Name); throw 'stub: service failed to start'" } else { "Add-Content '$svcLog' ('START ' + `$Name)" }
    $envSet = ($Env.GetEnumerator() | ForEach-Object { "`$env:$($_.Key) = '$($_.Value)'" }) -join "; "
    $argText = ($PhaseArgs | ForEach-Object { if ($_ -like '-*') { $_ } else { "'$_'" } }) -join " "
    $script = Join-Path $ToolsDir "server-postinstall.ps1"
    $cmd = "[IO.Directory]::SetCurrentDirectory('$tmp'); $envSet; " +
        "function global:Stop-Service { [CmdletBinding()] param([Parameter(Position=0)]`$Name) Add-Content '$svcLog' ('STOP ' + `$Name) }; " +
        "function global:Start-Service { [CmdletBinding()] param([Parameter(Position=0)]`$Name) $startBody }; " +
        "`$null = New-PSDrive -Name C -PSProvider FileSystem -Root '$cRoot' -Scope Global; `$env:ProgramData = 'C:\ProgramData'; " +
        "try { & '$script' $argText; exit `$LASTEXITCODE } catch { Write-Host ('SCRIPT-TERMINATING-ERROR: ' + `$_.Exception.Message); exit 1 }"
    $out = & pwsh -NoProfile -NonInteractive -Command $cmd 2>&1 | Out-String
    $rc = $LASTEXITCODE
    $trace = Join-Path $cRoot "ProgramData/Checkpoint/logs/postinstall-phase-trace.log"
    $r = [pscustomobject]@{
        Exit  = $rc
        Out   = $out
        Svc   = if (Test-Path $svcLog) { (Get-Content $svcLog) -join "," } else { "" }
        Trace = if (Test-Path $trace) { (Get-Content $trace) -join " | " } else { "" }
        Dumps = @(Get-ChildItem -Path (Join-Path $cRoot "ProgramData/Checkpoint/backups") -Filter "pre-upgrade-*.dump" -ErrorAction SilentlyContinue).Count
    }
    Remove-Item -Recurse -Force $tmp
    return $r
}
function Format-Leg($r) { "exit=$($r.Exit) svc=[$($r.Svc)] trace=[$($r.Trace)] out=[$((($r.Out -split "`n") | Where-Object { $_.Trim() } | Select-Object -Last 3) -join ' / ')]" }

# F6-1: upgrade-backup fails (pg_dump missing) => 143, server left stopped, never started, no dump, trace says left_stopped.
$r = Invoke-Leg -PhaseArgs @("-InstallMode", "upgrade", "-InstallPhase", "upgrade-backup")
$d = Format-Leg $r
Assert-Leg "F6 upgrade-backup failure exits 143" ($r.Exit -eq 143) $d
Assert-Leg "F6 upgrade-backup failure logs LEFT_STOPPED" ($r.Out -match "CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED reason=no_preupgrade_backup service=CheckpointServer action=") $d
Assert-Leg "F6 upgrade-backup failure never starts CheckpointServer" ($r.Svc -notmatch "START") $d
Assert-Leg "F6 upgrade-backup failure stops CheckpointServer" ($r.Svc -match "STOP CheckpointServer") $d
Assert-Leg "F6 upgrade-backup failure trace left_stopped + status=fail" (($r.Trace -match "upgrade_restore=left_stopped reason=no_preupgrade_backup") -and ($r.Trace -match "phase=upgrade-backup status=fail exit=143")) $d
Assert-Leg "F6 upgrade-backup failure writes no pre-upgrade dump" ($r.Dumps -eq 0) $d

# F6-2: stale/empty dump marker (dump file empty) must not count as a backup.
$r = Invoke-Leg -PhaseArgs @("-InstallMode", "upgrade", "-InstallPhase", "upgrade-backup") -Prepare {
    param($c)
    [IO.File]::WriteAllText((Join-Path $c "ProgramData/Checkpoint/backups/pre-upgrade-old-empty.dump"), "")
    [IO.File]::WriteAllText((Join-Path $c "ProgramData/Checkpoint/logs/preupgrade-dump.current"), "C:\ProgramData\Checkpoint\backups\pre-upgrade-old-empty.dump")
}
$d = Format-Leg $r
Assert-Leg "F6 empty dump behind marker => still left stopped" (($r.Exit -eq 143) -and ($r.Svc -notmatch "START") -and ($r.Out -match "UPGRADE_LEFT_STOPPED")) $d

# F6-3: upgrade-pins via hook (before any backup) => 141, left stopped, hook writes status=fail.
$r = Invoke-Leg -PhaseArgs @("-InstallMode", "upgrade", "-InstallPhase", "upgrade-pins") -Env @{ CHECKPOINT_INTERNAL_TEST_HOOKS = "1"; CHECKPOINT_TEST_FAIL_PHASE = "upgrade-pins" }
$d = Format-Leg $r
Assert-Leg "hook upgrade-pins exits 141 + status=fail in trace" (($r.Exit -eq 141) -and ($r.Trace -match "phase=upgrade-pins status=fail exit=141")) $d
Assert-Leg "hook upgrade-pins (no dump) leaves server stopped" (($r.Svc -notmatch "START") -and ($r.Out -match "UPGRADE_LEFT_STOPPED")) $d

# Restore-with-dump: backup succeeded earlier in this run (marker + non-empty dump), upgrade-start fails => 144, restore starts server.
$r = Invoke-Leg -PhaseArgs @("-InstallMode", "upgrade", "-InstallPhase", "upgrade-start") -StartServiceThrows -Prepare {
    param($c)
    [IO.File]::WriteAllText((Join-Path $c "ProgramData/Checkpoint/backups/pre-upgrade-1.0-20261126-1430.dump"), "PGDMP")
    [IO.File]::WriteAllText((Join-Path $c "ProgramData/Checkpoint/logs/preupgrade-dump.current"), "C:\ProgramData\Checkpoint\backups\pre-upgrade-1.0-20261126-1430.dump")
}
$d = Format-Leg $r
Assert-Leg "restore with dump: upgrade-start failure exits 144" ($r.Exit -eq 144) $d
Assert-Leg "restore with dump: RESTORE start line names the dump" ($r.Out -match [regex]::Escape("CHECKPOINT_POSTINSTALL_UPGRADE_RESTORE start CheckpointServer dump=C:\ProgramData\Checkpoint\backups\pre-upgrade-1.0-20261126-1430.dump")) $d
Assert-Leg "restore with dump: cleanup starts CheckpointServer again" ((($r.Svc -split ",") | Where-Object { $_ -eq "START CheckpointServer" }).Count -eq 2) $d
Assert-Leg "restore with dump: no LEFT_STOPPED" ($r.Out -notmatch "UPGRADE_LEFT_STOPPED") $d

# Hooks write status=fail to the trace (env hook and file hook), fresh-install phases.
$r = Invoke-Leg -PhaseArgs @("-InstallPhase", "database") -Env @{ CHECKPOINT_INTERNAL_TEST_HOOKS = "1"; CHECKPOINT_TEST_FAIL_PHASE = "database" }
Assert-Leg "env hook database => 135 + status=fail" (($r.Exit -eq 135) -and ($r.Trace -match "phase=database status=fail exit=135")) (Format-Leg $r)
$r = Invoke-Leg -PhaseArgs @("-InstallPhase", "firewall") -Env @{ CHECKPOINT_INTERNAL_TEST_HOOKS = "1" } -Prepare {
    param($c) [IO.File]::WriteAllText((Join-Path $c "ProgramData/Checkpoint/config/.internal-test-fail-phase"), "firewall")
}
Assert-Leg "file hook firewall => 138 + status=fail" (($r.Exit -eq 138) -and ($r.Trace -match "phase=firewall status=fail exit=138")) (Format-Leg $r)

Remove-Item -Recurse -Force $ToolsDir
Write-Host "SUMMARY pass=$($script:passes) fail=$($script:fails)"
if ($script:fails -gt 0) { exit 1 }
exit 0
