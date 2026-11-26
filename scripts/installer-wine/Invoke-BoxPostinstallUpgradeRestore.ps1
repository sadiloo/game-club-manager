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
    # Native children (fake pg_dump, hash stub) get C:/... paths: run them from $tmp, where 'C:' links to the C root.
    $cmd = "[IO.Directory]::SetCurrentDirectory('$tmp'); Set-Location -LiteralPath '$tmp'; $envSet; " +
        "function global:Stop-Service { [CmdletBinding()] param([Parameter(Position=0)]`$Name) Add-Content '$svcLog' ('STOP ' + `$Name) }; " +
        "function global:Start-Service { [CmdletBinding()] param([Parameter(Position=0)]`$Name) $startBody }; " +
        "function global:icacls { `$global:LASTEXITCODE = 0 }; " +
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
        Backups = (@(Get-ChildItem -Path (Join-Path $cRoot "ProgramData/Checkpoint/backups") -File -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object { "$($_.Name):$($_.Length)" }) -join ",")
        FailureIni = $(if (Test-Path -LiteralPath (Join-Path $cRoot "ProgramData/Checkpoint/logs/checkpoint-setup-failure.ini")) { Get-Content -LiteralPath (Join-Path $cRoot "ProgramData/Checkpoint/logs/checkpoint-setup-failure.ini") -Raw } else { "" })
        ServerJson = $(if (Test-Path -LiteralPath (Join-Path $cRoot "ProgramData/Checkpoint/config/server.json")) { Get-Content -LiteralPath (Join-Path $cRoot "ProgramData/Checkpoint/config/server.json") -Raw } else { "" })
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

# PO-F: the PowerShell LEFT_STOPPED line carries the exact recovery command (with -SetupExe from Inno).
$r = Invoke-Leg -PhaseArgs @("-InstallMode", "upgrade", "-InstallPhase", "upgrade-backup", "-SetupExe", "D:\setup\CheckpointServerSetup-9.9.9.exe")
Assert-Leg "PO-F LEFT_STOPPED prints recovery_command with /SKIPSESSIONCHECK=1" ($r.Out -match [regex]::Escape('CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED recovery_command="D:\setup\CheckpointServerSetup-9.9.9.exe" /SKIPSESSIONCHECK=1 /LOG="C:\ProgramData\Checkpoint\logs\setup-recovery.log"')) (Format-Leg $r)

# Item 7: a failed FIRST install stops both services and never starts one (fresh hook failure at 'start').
$r = Invoke-Leg -PhaseArgs @("-InstallPhase", "start") -Env @{ CHECKPOINT_INTERNAL_TEST_HOOKS = "1"; CHECKPOINT_TEST_FAIL_PHASE = "start" }
$d = Format-Leg $r
Assert-Leg "fresh start failure => 140 + ABORT_SERVICES_STOPPED" (($r.Exit -eq 140) -and ($r.Out -match "CHECKPOINT_POSTINSTALL_ABORT_SERVICES_STOPPED")) $d
Assert-Leg "fresh start failure stops CheckpointServer and CheckpointPostgres, starts nothing" (($r.Svc -eq "STOP CheckpointServer,STOP CheckpointPostgres") -and ($r.Svc -notmatch "START")) $d

# DEF-17: pg_dump fails after writing a partial pre-upgrade dump => that file is removed + logged; older dumps untouched.
$pgDumpScript = { param($c, [string] $body)
    $bin = Join-Path $c "Program Files/Checkpoint/PostgreSQL/bin"
    New-Item -ItemType Directory -Force -Path $bin | Out-Null
    $f = Join-Path $bin "pg_dump.exe"
    [IO.File]::WriteAllText($f, "#!/bin/sh`nout=`nwhile [ `$# -gt 0 ]; do case `"`$1`" in -f) out=`"`$2`"; shift 2;; *) shift;; esac; done`n$body`n")
    & chmod +x $f
}
$r = Invoke-Leg -PhaseArgs @("-InstallMode", "upgrade", "-InstallPhase", "upgrade-backup") -Prepare {
    param($c)
    & $pgDumpScript $c 'printf PARTIAL > "$out"; exit 3'
    [IO.File]::WriteAllText((Join-Path $c "ProgramData/Checkpoint/backups/pre-upgrade-unknown-20250101-0000.dump"), "PGDMP-previous")
    [IO.File]::WriteAllText((Join-Path $c "ProgramData/Checkpoint/backups/checkpoint-20250101-000000.000.dump"), "PGDMP-daily")
}
$d = "$(Format-Leg $r) backups=[$($r.Backups)]"
Assert-Leg "DEF-17 failed pre-upgrade pg_dump => 143 + left stopped" (($r.Exit -eq 143) -and ($r.Out -match "UPGRADE_LEFT_STOPPED") -and ($r.Svc -notmatch "START")) $d
Assert-Leg "DEF-17 own partial output removed + logged" ($r.Out -match "CHECKPOINT_PGDUMP_FAILED label=upgrade-backup exit=3 output=\S+pre-upgrade-unknown-\d{8}-\d{4}(-\d+)?\.dump bytes=7 action=removed_own_failed_output") $d
Assert-Leg "DEF-17 previous backups untouched, no new file left" ($r.Backups -eq "checkpoint-20250101-000000.000.dump:11,pre-upgrade-unknown-20250101-0000.dump:14") $d

# DEF-17: a pre-upgrade dump in the same minute never overwrites the previous one (new name -2).
$r = Invoke-Leg -PhaseArgs @("-InstallMode", "upgrade", "-InstallPhase", "upgrade-backup") -Prepare {
    param($c)
    & $pgDumpScript $c 'printf PGDMP-new > "$out"; exit 0'
    foreach ($t in @((Get-Date), (Get-Date).AddMinutes(1))) {
        [IO.File]::WriteAllText((Join-Path $c ("ProgramData/Checkpoint/backups/pre-upgrade-unknown-" + $t.ToString("yyyyMMdd-HHmm") + ".dump")), "PGDMP-old")
    }
}
$d = "$(Format-Leg $r) backups=[$($r.Backups)]"
$keptOld = @($r.Backups -split "," | Where-Object { $_ -match "^pre-upgrade-unknown-\d{8}-\d{4}\.dump:9$" }).Count
Assert-Leg "DEF-17 same-minute rerun: exit 0, both old dumps kept, new dump has a -2 name" (($r.Exit -eq 0) -and ($r.Backups -match "pre-upgrade-unknown-\d{8}-\d{4}-2\.dump:9") -and ($keptOld -eq 2)) $d

# DEF-13b: accounts phase writes role NAMES with at least one Manager; blank hash / missing accounts => 132.
$hashStub = { param($c, [string] $body)
    $f = Join-Path $c "hash-stub.sh"
    [IO.File]::WriteAllText($f, "#!/bin/sh`nread -r pin`n$body`n")
    & chmod +x $f
}
$accFile = Join-Path ([IO.Path]::GetTempPath()) ("cp-acc-" + [guid]::NewGuid().ToString("N").Substring(0, 8) + ".json")
'{"manager1User":"m1","manager1Pin":"5678","manager2User":"m2","manager2Pin":"8765","operatorUser":"op","operatorPin":"4321"}' | Set-Content -LiteralPath $accFile
$r = Invoke-Leg -PhaseArgs @("-InstallPhase", "accounts", "-AccountsFile", $accFile, "-ServerExe", "./c/hash-stub.sh") -Prepare {
    param($c) & $hashStub $c 'printf "hash:%s" "$pin"'
}
$d = Format-Leg $r
$roles = if ($r.ServerJson) { (($r.ServerJson | ConvertFrom-Json).InitialUsers | ForEach-Object { "$($_.username)=$($_.role)=$($_.pinHash)" }) -join "," } else { "" }
Assert-Leg "accounts phase: exit 0, InitialUsers = role names incl. Manager, hashed PINs" (($r.Exit -eq 0) -and ($roles -eq "m1=Manager=hash:5678,m2=Manager=hash:8765,op=Operator=hash:4321")) "$d roles=[$roles]"
$r = Invoke-Leg -PhaseArgs @("-InstallPhase", "accounts", "-AccountsFile", $accFile, "-ServerExe", "./c/hash-stub.sh") -Prepare {
    param($c) & $hashStub $c 'printf "   "'
}
Assert-Leg "accounts phase: blank PIN hash => 132 (never seeds an empty hash)" (($r.Exit -eq 132) -and ($r.FailureIni -match "blank pin hash for manager1") -and -not $r.ServerJson) "$(Format-Leg $r) ini=[$($r.FailureIni)]"
$r = Invoke-Leg -PhaseArgs @("-InstallPhase", "accounts", "-AccountsFile", "/nonexistent/accounts.json", "-ServerExe", "./c/hash-stub.sh")
Assert-Leg "accounts phase: no accounts file => 132, no server.json (server would refuse: no Manager)" (($r.Exit -eq 132) -and -not $r.ServerJson) (Format-Leg $r)
Remove-Item -LiteralPath $accFile -Force

Remove-Item -Recurse -Force $ToolsDir
Write-Host "SUMMARY pass=$($script:passes) fail=$($script:fails)"
if ($script:fails -gt 0) { exit 1 }
exit 0
