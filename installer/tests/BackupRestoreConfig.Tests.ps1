$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $root "installer\tools\CheckpointDbConfig.ps1")
$goldenRestore = Join-Path $root "installer\tools\restore.ps1"
$goldenBackup = Join-Path $root "installer\tools\backup-daily.ps1"
$script:StubDirs = [System.Collections.Generic.List[string]]::new()

function New-StubPgBinaries {
    param(
        [int]$PgDumpExit = 0,
        [int]$FirstListExit = 0,
        [int]$SecondListExit = 0,
        [int]$RestoreExit = 0,
        [int]$PsqlExit = 0,
        # R10h: >= 0 makes `pg_restore --list` of a pre-restore-* (safety) dump exit with this code and every other
        # --list exit 0, independent of call order (needed for mutants that drop or reorder a readability check).
        [int]$SafetyListExit = -1,
        [switch]$PgDumpWriteOnFailure,
        [switch]$PgDumpWriteZeroBytes
    )
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("gcm-stub-pg-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $script:StubDirs.Add($dir) | Out-Null
    $listTrace = Join-Path $dir "list-trace.txt"
    $cleanTrace = Join-Path $dir "clean-trace.txt"
    # R10h: one ordered trace across the stubs: DUMP (pg_dump), LIST (pg_restore --list), RECREATE (psql),
    # RESTORE / RESTORE_CLEAN (pg_restore into the database, without / with --clean).
    $orderTrace = Join-Path $dir "order-trace.txt"
    $listPass = Join-Path $dir "list.pass"

    $writeOnFail = if ($PgDumpWriteOnFailure) { "1" } else { "0" }
    $writeZero = if ($PgDumpWriteZeroBytes) { "1" } else { "0" }
    if ($IsWindows -or ($env:OS -eq 'Windows_NT')) {
        @"
@echo off
setlocal EnableDelayedExpansion
echo DUMP>> "$orderTrace"
set "outfile="
:parse
if "%~1"=="" goto done
if /I "%~1"=="-f" (
  set "outfile=%~2"
  shift
)
shift
goto parse
:done
if defined outfile (
  if $PgDumpExit equ 0 (
    if $writeZero equ 1 (
      break>"!outfile!"
    ) else (
      echo.>"!outfile!"
    )
    exit /b 0
  )
  if $writeOnFail equ 1 echo.>"!outfile!"
)
exit /b $PgDumpExit
"@ | Set-Content -LiteralPath (Join-Path $dir "pg_dump.cmd") -Encoding ASCII

        @"
@echo off
setlocal EnableDelayedExpansion
set "args=%*"
echo %args% | findstr /I /C:"--list" >nul
if %errorlevel% equ 0 (
  echo LIST>> "$orderTrace"
  if $SafetyListExit geq 0 (
    echo LIST>> "$listTrace"
    echo %args% | findstr /I /C:"pre-restore-" >nul
    if !errorlevel! equ 0 exit /b $SafetyListExit
    exit /b 0
  )
  if not exist "$listPass" (
    echo LIST>> "$listTrace"
    echo.>"$listPass"
    exit /b $FirstListExit
  ) else (
    echo LIST>> "$listTrace"
    exit /b $SecondListExit
  )
)
echo CLEAN>> "$cleanTrace"
echo %args% | findstr /I /C:"--clean" >nul
if %errorlevel% equ 0 (
  echo RESTORE_CLEAN>> "$orderTrace"
) else (
  echo RESTORE>> "$orderTrace"
)
exit /b $RestoreExit
"@ | Set-Content -LiteralPath (Join-Path $dir "pg_restore.cmd") -Encoding ASCII

        @"
@echo off
echo RECREATE>> "$orderTrace"
exit /b $PsqlExit
"@ | Set-Content -LiteralPath (Join-Path $dir "psql.cmd") -Encoding ASCII
    } else {
        @"
#!/bin/sh
echo DUMP >> '$orderTrace'
outfile=
write_on_fail=$writeOnFail
write_zero=$writeZero
while [ `$# -gt 0 ]; do
  case "`$1" in
    -f) outfile="`$2"; shift 2 ;;
    *) shift ;;
  esac
done
if [ -n "`$outfile" ]; then
  if [ $PgDumpExit -eq 0 ]; then
    if [ "`$write_zero" = "1" ]; then
      : > "`$outfile"
    else
      printf '\001' > "`$outfile"
    fi
  elif [ "`$write_on_fail" = "1" ]; then
    printf '\001' > "`$outfile"
  fi
fi
exit $PgDumpExit
"@ | Set-Content -LiteralPath (Join-Path $dir "pg_dump") -Encoding UTF8 -NoNewline
        [System.IO.File]::AppendAllText((Join-Path $dir "pg_dump"), "`n")

        @"
#!/bin/sh
clean=0
for a in "`$@"; do
  case "`$a" in
    --list)
      echo LIST >> '$orderTrace'
      if [ $SafetyListExit -ge 0 ]; then
        echo LIST >> '$listTrace'
        case "`$*" in *pre-restore-*) exit $SafetyListExit ;; esac
        exit 0
      fi
      if [ ! -f '$listPass' ]; then
        echo LIST >> '$listTrace'
        touch '$listPass'
        exit $FirstListExit
      fi
      echo LIST >> '$listTrace'
      exit $SecondListExit
      ;;
    --clean) clean=1 ;;
  esac
done
echo CLEAN >> '$cleanTrace'
if [ "`$clean" = "1" ]; then echo RESTORE_CLEAN >> '$orderTrace'; else echo RESTORE >> '$orderTrace'; fi
exit $RestoreExit
"@ | Set-Content -LiteralPath (Join-Path $dir "pg_restore") -Encoding UTF8 -NoNewline
        [System.IO.File]::AppendAllText((Join-Path $dir "pg_restore"), "`n")

        @"
#!/bin/sh
echo RECREATE >> '$orderTrace'
exit $PsqlExit
"@ | Set-Content -LiteralPath (Join-Path $dir "psql") -Encoding UTF8 -NoNewline
        [System.IO.File]::AppendAllText((Join-Path $dir "psql"), "`n")
        foreach ($name in @('pg_dump', 'pg_restore', 'psql')) {
            & chmod +x (Join-Path $dir $name)
        }
    }

    return [pscustomobject]@{
        Dir        = $dir
        ListTrace  = $listTrace
        CleanTrace = $cleanTrace
        OrderTrace = $orderTrace
    }
}

function Invoke-WithStubPath {
    param(
        [pscustomobject]$Stub,
        [scriptblock]$Action
    )
    $prevPath = $env:PATH
    $env:PATH = "$($Stub.Dir)" + [IO.Path]::PathSeparator + $env:PATH
    try {
        return & $Action
    } finally {
        $env:PATH = $prevPath
    }
}

function Invoke-RestoreScript {
    param(
        [string]$ScriptPath,
        [string]$DumpPath,
        [string]$ServerJsonPath,
        [string]$WorkProgramData,
        [pscustomobject]$Stub
    )
    function Stop-Service { param([string]$Name, $ErrorAction) }
    function Start-Service { param([string]$Name, $ErrorAction) }

    $prevPd = $env:ProgramData
    $env:ProgramData = $WorkProgramData
    try {
        return Invoke-WithStubPath -Stub $Stub -Action {
            $recorded = $null
            try {
                & $ScriptPath -DumpPath $DumpPath -ServerJsonPath $ServerJsonPath 2>&1 | Out-Null
            } catch {
                $recorded = $_
            }
            $list = ''
            $order = ''
            if (Test-Path -LiteralPath $Stub.ListTrace) { $list = Get-Content -LiteralPath $Stub.ListTrace -Raw }
            if (Test-Path -LiteralPath $Stub.OrderTrace) { $order = Get-Content -LiteralPath $Stub.OrderTrace -Raw }
            $steps = @($order -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
            return [pscustomobject]@{
                Error     = $recorded
                # R10h: "the database was touched" = the schema recreate (psql) or a restore into the DB ran.
                CleanRan  = [bool](@($steps | Where-Object { $_ -in @('RECREATE', 'RESTORE', 'RESTORE_CLEAN') }).Count)
                ListCalls = @($list -split "`n" | Where-Object { $_ -match '\S' }).Count
                Order     = ($steps -join ',')
            }
        }
    } finally {
        $env:ProgramData = $prevPd
    }
}

function New-ScriptMutant {
    param(
        [string]$SourcePath,
        [string]$Tag,
        [scriptblock]$Transform,
        [string]$WorkDir
    )
    # R10h: each mutant lives in its own folder WITH the CheckpointDbConfig.ps1 it dot-sources. Before R10h the mutant
    # was written straight into $WorkDir (no CheckpointDbConfig.ps1), crashed on its first line, and every in-file
    # mutant below counted as "killed" without running.
    $mutDir = Join-Path $WorkDir "mutant-$Tag"
    New-Item -ItemType Directory -Force -Path $mutDir | Out-Null
    Copy-Item -LiteralPath (Join-Path (Split-Path $SourcePath -Parent) "CheckpointDbConfig.ps1") -Destination $mutDir
    $dest = Join-Path $mutDir ([System.IO.Path]::GetFileName($SourcePath))
    $original = Get-Content -LiteralPath $SourcePath -Raw
    $text = & $Transform $original
    if ($text -eq $original) {
        throw "mutant transform made no change: $Tag"
    }
    Set-Content -LiteralPath $dest -Value $text -Encoding UTF8
    return $dest
}

function Assert-MutantKilled {
    param(
        [string]$Name,
        [string]$SourcePath,
        [scriptblock]$Transform,
        [scriptblock]$GoldenSafe,
        [scriptblock]$MutantUnsafe,
        [string]$WorkDir
    )
    try {
        $goldenOk = & $GoldenSafe $SourcePath
    } catch {
        Write-Error "golden scenario failed for ${Name}: $($_.Exception.Message)"
    }
    if (-not $goldenOk) {
        Write-Error "golden scenario failed for ${Name}"
    }
    $mutant = New-ScriptMutant -SourcePath $SourcePath -Tag $Name -Transform $Transform -WorkDir $WorkDir
    # R10h: killed = the same check that passes on the golden script FAILS on the mutant, AND the mutant really showed
    # the unsafe behaviour (-MutantUnsafe returns $true), so a mutant that merely crashes no longer counts as killed.
    $mutantPassesCheck = $false
    try { $mutantPassesCheck = [bool](& $GoldenSafe $mutant) } catch { $mutantPassesCheck = $false }
    if ($mutantPassesCheck) {
        Write-Error "mutant not killed: $Name"
    }
    $mutantShowedUnsafe = $false
    try { $mutantShowedUnsafe = [bool](& $MutantUnsafe $mutant) } catch { $mutantShowedUnsafe = $false }
    if (-not $mutantShowedUnsafe) {
        Write-Error "mutant ${Name}: did not show the unsafe behaviour (crashed or transform ineffective), so the kill proves nothing"
    }
    Write-Host "mutant killed: $Name"
}

function Invoke-BackupScript {
    param(
        [string]$ScriptPath,
        [string]$BackupDir,
        [string]$ServerJsonPath,
        [string]$LogsDir,
        [pscustomobject]$Stub
    )
    Invoke-WithStubPath -Stub $Stub -Action {
        $threw = $null
        try {
            & $ScriptPath -PrimaryBackupDir $BackupDir -ServerJsonPath $ServerJsonPath -LogsDir $LogsDir 2>&1 | Out-Null
        } catch {
            $threw = $_
        }
        return $threw
    }
}

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gcm-br-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $workDir | Out-Null
try {
    $jsonPath = Join-Path $workDir "server.json"
    @{
        ConnectionStrings = @{
            Default = "Host=127.0.0.1;Port=5433;Database=gameclub;Username=postgres;Password=secret"
        }
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

    $cfg = Read-CheckpointServerJsonConfig -ServerJsonPath $jsonPath
    if ($cfg.Database -ne "gameclub" -or $cfg.Port -ne "5433") {
        Write-Error "unexpected parsed config"
    }

    $emptyDump = Join-Path $workDir "empty.dump"
    [System.IO.File]::WriteAllBytes($emptyDump, [byte[]]@())
    $emptyThrown = $false
    try {
        Test-PostgresDumpReadable -DumpPath $emptyDump
    } catch [System.Management.Automation.RuntimeException] {
        if ($_.Exception.Message -notmatch '^Dump file is empty:') { throw }
        $emptyThrown = $true
    }
    if (-not $emptyThrown) {
        Write-Error "expected empty dump to throw Dump file is empty"
    }

    $backupDir = Join-Path $workDir "backups"
    $logsDir = Join-Path $workDir "logs"
    $stubGood = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 0
    $env:ProgramData = $workDir
    New-Item -ItemType Directory -Force -Path (Join-Path $workDir "Checkpoint\config") | Out-Null
    Copy-Item -LiteralPath $jsonPath -Destination (Join-Path $workDir "Checkpoint\config\server.json")
    $serverJson = Join-Path $workDir "Checkpoint\config\server.json"
    Invoke-WithStubPath -Stub $stubGood -Action {
        & $goldenBackup -PrimaryBackupDir $backupDir -ServerJsonPath $serverJson -LogsDir $logsDir
        if (-not (Test-Path -LiteralPath $backupDir)) {
            Write-Error "backup dir was not created"
        }
    }

    $stubDumpFail = New-StubPgBinaries -PgDumpExit 3
    $err = Invoke-BackupScript -ScriptPath $goldenBackup -BackupDir (Join-Path $workDir "backups-fail") -ServerJsonPath $jsonPath -LogsDir (Join-Path $workDir "logs2") -Stub $stubDumpFail
    if (-not $err) { Write-Error "expected pg_dump failure to abort backup" }
    if ($err.Exception.Message -notmatch 'pg_dump failed with exit code 3') { throw $err }

    # DEF-17: pg_dump fails after writing a partial file -> that file (and only that file) is removed + logged.
    $partialDir = Join-Path $workDir "backups-partial"
    $partialLogs = Join-Path $workDir "logs-partial"
    New-Item -ItemType Directory -Force -Path $partialDir | Out-Null
    $previousDump = Join-Path $partialDir "checkpoint-20250101-000000.000.dump"
    $previousPre = Join-Path $partialDir "pre-upgrade-0.0.9-20250101-0000.dump"
    [System.IO.File]::WriteAllBytes($previousDump, [byte[]]@(1, 2, 3))
    [System.IO.File]::WriteAllBytes($previousPre, [byte[]]@(4, 5))
    $stubPartial = New-StubPgBinaries -PgDumpExit 3 -PgDumpWriteOnFailure
    $err = Invoke-BackupScript -ScriptPath $goldenBackup -BackupDir $partialDir -ServerJsonPath $jsonPath -LogsDir $partialLogs -Stub $stubPartial
    if (-not $err -or $err.Exception.Message -notmatch 'pg_dump failed with exit code 3') { Write-Error "DEF-17: expected pg_dump exit 3 failure; got $err" }
    $left = @(Get-ChildItem -LiteralPath $partialDir -File | ForEach-Object { $_.Name } | Sort-Object)
    if (($left -join ',') -ne 'checkpoint-20250101-000000.000.dump,pre-upgrade-0.0.9-20250101-0000.dump') {
        Write-Error "DEF-17: failed pg_dump must remove only its own partial output; left=[$($left -join ',')]"
    }
    if ((Get-Item -LiteralPath $previousDump).Length -ne 3 -or (Get-Item -LiteralPath $previousPre).Length -ne 2) {
        Write-Error "DEF-17: previous backups must be untouched"
    }
    $partialLog = Get-Content -LiteralPath (Join-Path $partialLogs "backup.log") -Raw
    if ($partialLog -notmatch 'CHECKPOINT_PGDUMP_FAILED label=backup-daily exit=3 output=\S+checkpoint-\S+\.dump bytes=[1-9]\d* action=removed_own_failed_output') {
        Write-Error "DEF-17: backup.log must log the removed partial output; log=$partialLog"
    }

    $stubBackupEmpty = New-StubPgBinaries -PgDumpExit 0 -PgDumpWriteZeroBytes
    $err = Invoke-BackupScript -ScriptPath $goldenBackup -BackupDir (Join-Path $workDir "backups-empty") -ServerJsonPath $jsonPath -LogsDir (Join-Path $workDir "logs-empty") -Stub $stubBackupEmpty
    if (-not $err) { Write-Error "expected zero-byte pg_dump output to abort backup" }
    if ($err.Exception.Message -notmatch '^Dump file is empty:') {
        Write-Error "expected Dump file is empty message; got $($err.Exception.Message)"
    }
    $emptyLeft = @(Get-ChildItem -LiteralPath (Join-Path $workDir "backups-empty") -File)
    if ($emptyLeft.Count -ne 0) { Write-Error "DEF-17: 0-byte pg_dump output must be removed; left=$($emptyLeft.Name -join ',')" }

    # DEF-17: a new dump never overwrites an existing name.
    $nameDir = Join-Path $workDir "names"
    New-Item -ItemType Directory -Force -Path $nameDir | Out-Null
    $n1 = Get-CheckpointNewDumpPath -Directory $nameDir -Stem "pre-upgrade-1-20250101-0000"
    [System.IO.File]::WriteAllBytes($n1, [byte[]]@(1))
    $n2 = Get-CheckpointNewDumpPath -Directory $nameDir -Stem "pre-upgrade-1-20250101-0000"
    if ((Split-Path $n1 -Leaf) -ne 'pre-upgrade-1-20250101-0000.dump' -or (Split-Path $n2 -Leaf) -ne 'pre-upgrade-1-20250101-0000-2.dump') {
        Write-Error "DEF-17: Get-CheckpointNewDumpPath must pick a fresh name; got $n1 / $n2"
    }
    $refused = $false
    try { $null = Invoke-CheckpointPgDumpNewFile -PgDump "pg_dump-must-not-run" -PgArgs @("-Fc") -OutFile $n1 -Log { param($l) } } catch { $refused = $_.Exception.Message -match 'refusing to write over an existing file' }
    if (-not $refused -or (Get-Item -LiteralPath $n1).Length -ne 1) { Write-Error "DEF-17: Invoke-CheckpointPgDumpNewFile must refuse an existing target" }

    $stubBackupListFail = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 11
    $err = Invoke-BackupScript -ScriptPath $goldenBackup -BackupDir (Join-Path $workDir "backups-listfail") -ServerJsonPath $jsonPath -LogsDir (Join-Path $workDir "logs-listfail") -Stub $stubBackupListFail
    if (-not $err) { Write-Error "expected pg_restore --list failure to abort backup" }
    if ($err.Exception.Message -notmatch '^pg_restore --list failed for ') {
        Write-Error "expected pg_restore --list failed message; got $($err.Exception.Message)"
    }

    Assert-MutantKilled -Name 'backup-no-post-dump-readable' -SourcePath $goldenBackup -WorkDir $workDir -Transform {
        param($t)
        $t -replace '(?ms)\r?\n\s*Test-PostgresDumpReadable -DumpPath \$dump\r?\n', "`n"
    } -GoldenSafe {
        param([string]$ScriptPath)
        # R10h: the empty-output case is caught earlier (DEF-20 "Dump file is empty"), so the line this mutant removes
        # guards a non-empty dump that fails `pg_restore --list`.
        $stub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 11
        $err = Invoke-BackupScript -ScriptPath $ScriptPath -BackupDir (Join-Path $workDir "bk-g-old") -ServerJsonPath $jsonPath -LogsDir (Join-Path $workDir "lg-g-old") -Stub $stub
        return ($null -ne $err -and $err.Exception.Message -match '^pg_restore --list failed for ')
    } -MutantUnsafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 11
        $err = Invoke-BackupScript -ScriptPath $ScriptPath -BackupDir (Join-Path $workDir "bk-m-old") -ServerJsonPath $jsonPath -LogsDir (Join-Path $workDir "lg-m-old") -Stub $stub
        return $null -eq $err
    }

    $inputDump = Join-Path $workDir "input.dump"
    [System.IO.File]::WriteAllBytes($inputDump, [byte[]](1, 2, 3))
    New-Item -ItemType Directory -Force -Path (Join-Path $workDir "Checkpoint\backups") | Out-Null

    $safetyDumpFailStub = New-StubPgBinaries -PgDumpExit 4
    $r = Invoke-RestoreScript -ScriptPath $goldenRestore -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $safetyDumpFailStub
    if (-not $r.Error) { Write-Error "expected safety pg_dump exit failure" }
    if ($r.Error.Exception.Message -notmatch 'Safety pg_dump failed with exit code 4') {
        Write-Error "unexpected safety pg_dump message: $($r.Error.Exception.Message)"
    }
    if ($r.CleanRan) { Write-Error "restore must not touch the database when safety pg_dump fails" }

    # DEF-20: a failed / empty SAFETY dump removes only its own new file; earlier backups stay byte-identical.
    $bkDir = Join-Path $workDir "Checkpoint\backups"
    $goodOld = Join-Path $bkDir "pre-restore-20250101-000000.dump"
    $goodCp = Join-Path $bkDir "checkpoint-20250101-000000.000.dump"
    [System.IO.File]::WriteAllBytes($goodOld, [byte[]](7, 7, 7))
    [System.IO.File]::WriteAllBytes($goodCp, [byte[]](8, 8, 8, 8))
    $restoreLogPath = Join-Path $workDir "Checkpoint\logs\restore.log"
    foreach ($case in @(
            @{ Name = "partial"; Stub = (New-StubPgBinaries -PgDumpExit 4 -PgDumpWriteOnFailure); Msg = 'Safety pg_dump failed with exit code 4'; LogRe = 'CHECKPOINT_PGDUMP_FAILED label=restore-safety exit=4 output=\S+pre-restore-\d{8}-\d{6}(-\d+)?\.dump bytes=[1-9]\d* action=removed_own_failed_output' },
            @{ Name = "empty"; Stub = (New-StubPgBinaries -PgDumpExit 0 -PgDumpWriteZeroBytes); Msg = 'Safety pg_dump wrote an empty file'; LogRe = 'CHECKPOINT_PGDUMP_FAILED label=restore-safety empty_output output=\S+pre-restore-\d{8}-\d{6}(-\d+)?\.dump bytes=0 action=removed_own_failed_output' })) {
        Remove-Item -LiteralPath $restoreLogPath -Force -ErrorAction SilentlyContinue
        $r = Invoke-RestoreScript -ScriptPath $goldenRestore -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $case.Stub
        if (-not $r.Error -or $r.Error.Exception.Message -notmatch [regex]::Escape($case.Msg)) { Write-Error "DEF-20 $($case.Name): expected '$($case.Msg)'; got $($r.Error)" }
        if ($r.CleanRan) { Write-Error "DEF-20 $($case.Name): restore must not touch the database" }
        $left = @(Get-ChildItem -LiteralPath $bkDir -File | ForEach-Object { $_.Name } | Sort-Object)
        $expect = @("checkpoint-20250101-000000.000.dump", "pre-restore-20250101-000000.dump")
        if (($left -join ",") -ne ($expect -join ",")) { Write-Error "DEF-20 $($case.Name): only the failed safety dump may be removed; left=[$($left -join ',')]" }
        if ((([System.IO.File]::ReadAllBytes($goodOld)) -join ",") -ne "7,7,7" -or (([System.IO.File]::ReadAllBytes($goodCp)) -join ",") -ne "8,8,8,8") { Write-Error "DEF-20 $($case.Name): earlier backups changed" }
        $logText = if (Test-Path -LiteralPath $restoreLogPath) { Get-Content -LiteralPath $restoreLogPath -Raw } else { "" }
        if ($logText -notmatch $case.LogRe) { Write-Error "DEF-20 $($case.Name): restore.log lacks /$($case.LogRe)/; got [$logText]" }
    }
    Remove-Item -LiteralPath $goodOld, $goodCp -Force

    # DEF-23 (ruling: an unreadable dump is a failed dump under PO-3/PO-G): pg_dump exits 0 but the same-run safety dump
    # fails `pg_restore --list`. Restore aborts before touching the database, removes THAT dump with a log line, and leaves every older
    # dump byte-identical.
    $def23 = {
        param([string]$RestorePath, [string]$Tag)
        $problems = [System.Collections.Generic.List[string]]::new()
        Get-ChildItem -LiteralPath $bkDir -File -ErrorAction SilentlyContinue | Remove-Item -Force
        [System.IO.File]::WriteAllBytes($goodOld, [byte[]](7, 7, 7))
        [System.IO.File]::WriteAllBytes($goodCp, [byte[]](8, 8, 8, 8))
        Remove-Item -LiteralPath $restoreLogPath -Force -ErrorAction SilentlyContinue
        $stub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 6
        $rr = Invoke-RestoreScript -ScriptPath $RestorePath -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $stub
        if (-not $rr.Error -or $rr.Error.Exception.Message -notmatch 'Safety dump is unreadable') { $problems.Add("expected 'Safety dump is unreadable'; got $($rr.Error)") }
        if ($rr.CleanRan) { $problems.Add("restore touched the database") }
        $left = @(Get-ChildItem -LiteralPath $bkDir -File | ForEach-Object { $_.Name } | Sort-Object)
        if (($left -join ",") -ne "checkpoint-20250101-000000.000.dump,pre-restore-20250101-000000.dump") { $problems.Add("the same-run unreadable safety dump must be removed and nothing else; left=[$($left -join ',')]") }
        if ((([System.IO.File]::ReadAllBytes($goodOld)) -join ",") -ne "7,7,7" -or (([System.IO.File]::ReadAllBytes($goodCp)) -join ",") -ne "8,8,8,8") { $problems.Add("older dumps changed") }
        $logText = if (Test-Path -LiteralPath $restoreLogPath) { Get-Content -LiteralPath $restoreLogPath -Raw } else { "" }
        $re = 'CHECKPOINT_PGDUMP_FAILED label=restore-safety unreadable output=\S+pre-restore-\d{8}-\d{6}(-\d+)?\.dump bytes=[1-9]\d* action=removed_own_failed_output'
        if ($logText -notmatch $re) { $problems.Add("restore.log lacks /$re/; got [$logText]") }
        return $problems.ToArray()
    }
    $def23Problems = @(& $def23 $goldenRestore "golden")
    if ($def23Problems.Count -gt 0) { Write-Error "DEF-23: $($def23Problems -join ' | ')" }
    # Mutant: the pre-R10g restore.ps1 shape (readability checked after the helper, no -VerifyReadable) must fail it.
    $def23MutDir = Join-Path $workDir "def23-mutant"
    New-Item -ItemType Directory -Force -Path $def23MutDir | Out-Null
    Copy-Item -LiteralPath (Join-Path (Split-Path $goldenRestore -Parent) "CheckpointDbConfig.ps1") -Destination $def23MutDir
    $def23MutPath = Join-Path $def23MutDir "restore.ps1"
    $restoreSrc = [System.IO.File]::ReadAllText($goldenRestore)
    $restoreMut = $restoreSrc.Replace(' -VerifyReadable -Log $restoreLogLine', ' -Log $restoreLogLine')
    if ($restoreMut -eq $restoreSrc) { Write-Error "DEF-23 mutant: -VerifyReadable not found in restore.ps1" }
    $restoreMut = $restoreMut.Replace("try {`r`n    Test-PostgresDumpReadable -DumpPath `$DumpPath", "try { Test-PostgresDumpReadable -DumpPath `$pre } catch { Write-Error ""Safety dump is unreadable (`$(`$_.Exception.Message)); restore aborted before --clean.""; exit 1 }`r`ntry {`r`n    Test-PostgresDumpReadable -DumpPath `$DumpPath").Replace("try {`n    Test-PostgresDumpReadable -DumpPath `$DumpPath", "try { Test-PostgresDumpReadable -DumpPath `$pre } catch { Write-Error ""Safety dump is unreadable (`$(`$_.Exception.Message)); restore aborted before --clean.""; exit 1 }`ntry {`n    Test-PostgresDumpReadable -DumpPath `$DumpPath")
    [System.IO.File]::WriteAllText($def23MutPath, $restoreMut, [System.Text.UTF8Encoding]::new($true))
    try {
        $mutProblems = @(& $def23 $def23MutPath "mutant")
        if ($mutProblems.Count -eq 0) { Write-Error "DEF-23 mutant (safety dump kept) was not caught" }
        elseif (-not ($mutProblems -match 'must be removed')) { Write-Error "DEF-23 mutant caught for the wrong reason: $($mutProblems -join ' | ')" }
        else { Write-Host "DEF-23 mutant (same-run unreadable safety dump kept) caught: $($mutProblems[0])" }
    } finally { Remove-Item -LiteralPath $def23MutDir -Recurse -Force -ErrorAction SilentlyContinue }
    Get-ChildItem -LiteralPath $bkDir -File -ErrorAction SilentlyContinue | Remove-Item -Force

    $inputUnreadStub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 0 -SecondListExit 7
    $r = Invoke-RestoreScript -ScriptPath $goldenRestore -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $inputUnreadStub
    if (-not $r.Error) { Write-Error "expected input dump readability failure" }
    if ($r.Error.Exception.Message -notmatch 'Input dump is unreadable') {
        Write-Error "unexpected input readability message: $($r.Error.Exception.Message)"
    }
    if ($r.ListCalls -lt 2) { Write-Error "expected input validation to run pg_restore --list after safety list" }
    if ($r.CleanRan) { Write-Error "restore must not touch the database when input dump is unreadable" }

    # R10h BL-rr6-2 (ruling): the schema is recreated only AFTER the safety dump was written and verified readable
    # (and the input dump verified), then the dump is restored WITHOUT --clean, and both steps are logged.
    $goodStub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 0 -SecondListExit 0
    Remove-Item -LiteralPath $restoreLogPath -Force -ErrorAction SilentlyContinue
    $r = Invoke-RestoreScript -ScriptPath $goldenRestore -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $goodStub
    if ($r.Error) { Write-Error "R10h: golden restore failed: $($r.Error)" }
    if ($r.Order -ne 'DUMP,LIST,LIST,RECREATE,RESTORE') { Write-Error "R10h: expected order DUMP,LIST,LIST,RECREATE,RESTORE (safety dump, verify safety, verify input, recreate, restore without --clean); got [$($r.Order)]" }
    $logText = Get-Content -LiteralPath $restoreLogPath -Raw
    $recreateRe = 'CHECKPOINT_RESTORE_SCHEMA_RECREATE database=gameclub safety_dump=\S+pre-restore-\d{8}-\d{6}(-\d+)?\.dump input_dump=\S+input\.dump action=drop_and_create_public_schema'
    if ($logText -notmatch $recreateRe) { Write-Error "R10h: restore.log lacks /$recreateRe/; got [$logText]" }
    if ($logText -notmatch 'CHECKPOINT_RESTORE_DONE database=gameclub ') { Write-Error "R10h: restore.log lacks CHECKPOINT_RESTORE_DONE; got [$logText]" }
    $restoreSrcText = [System.IO.File]::ReadAllText($goldenRestore)
    if ($restoreSrcText -match '(?m)^\s*& \$pgRestore[^\r\n]*--clean') { Write-Error "R10h: restore.ps1 must not call pg_restore --clean any more" }
    if ($restoreSrcText -notmatch [regex]::Escape('-v ON_ERROR_STOP=1 -c "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public AUTHORIZATION pg_database_owner; GRANT USAGE ON SCHEMA public TO PUBLIC;"')) { Write-Error "R10h: restore.ps1 recreate statement changed" }

    $psqlFailStub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 0 -SecondListExit 0 -PsqlExit 5
    Remove-Item -LiteralPath $restoreLogPath -Force -ErrorAction SilentlyContinue
    $r = Invoke-RestoreScript -ScriptPath $goldenRestore -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $psqlFailStub
    if (-not $r.Error -or $r.Error.Exception.Message -notmatch 'Recreating the database schema failed with exit code 5; restore aborted\. Safety dump: \S+pre-restore-') { Write-Error "R10h: expected schema-recreate failure; got $($r.Error)" }
    if ($r.Order -ne 'DUMP,LIST,LIST,RECREATE') { Write-Error "R10h: a failed recreate must not restore; got [$($r.Order)]" }
    if ((Get-Content -LiteralPath $restoreLogPath -Raw) -notmatch 'CHECKPOINT_RESTORE_SCHEMA_RECREATE_FAILED exit=5 ') { Write-Error "R10h: restore.log lacks the recreate failure line" }

    $restoreFailStub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 0 -SecondListExit 0 -RestoreExit 9
    Remove-Item -LiteralPath $restoreLogPath -Force -ErrorAction SilentlyContinue
    $r = Invoke-RestoreScript -ScriptPath $goldenRestore -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $restoreFailStub
    if (-not $r.Error) { Write-Error "expected pg_restore failure" }
    if ($r.Error.Exception.Message -notmatch 'pg_restore failed with exit code 9\. The schema was recreated; to go back, run restore\.ps1 -DumpPath "\S+pre-restore-') {
        Write-Error "unexpected pg_restore failure message: $($r.Error.Exception.Message)"
    }
    if ($r.Order -ne 'DUMP,LIST,LIST,RECREATE,RESTORE') { Write-Error "R10h: restore-failure order; got [$($r.Order)]" }
    if ((Get-Content -LiteralPath $restoreLogPath -Raw) -notmatch 'CHECKPOINT_RESTORE_FAILED exit=9 database=gameclub safety_dump=\S+ recovery=restore\.ps1 -DumpPath ') { Write-Error "R10h: restore.log lacks the restore failure line" }
    # Safety / input failures never reach RECREATE (pinned by the order, not just by "no restore").
    foreach ($case in @(
            @{ Name = 'safety-exit'; Stub = (New-StubPgBinaries -PgDumpExit 4) ; Want = 'DUMP' },
            @{ Name = 'safety-unreadable'; Stub = (New-StubPgBinaries -PgDumpExit 0 -FirstListExit 6); Want = 'DUMP,LIST' },
            @{ Name = 'input-unreadable'; Stub = (New-StubPgBinaries -PgDumpExit 0 -FirstListExit 0 -SecondListExit 7); Want = 'DUMP,LIST,LIST' })) {
        $r = Invoke-RestoreScript -ScriptPath $goldenRestore -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $case.Stub
        if (-not $r.Error -or $r.Order -ne $case.Want) { Write-Error "R10h $($case.Name): expected abort with order [$($case.Want)] (database untouched); got [$($r.Order)] error=$($r.Error)" }
    }
    Get-ChildItem -LiteralPath $bkDir -File -ErrorAction SilentlyContinue | Remove-Item -Force

    Assert-MutantKilled -Name 'no-safety-readable' -SourcePath $goldenRestore -WorkDir $workDir -Transform {
        param($t)
        # R10g DEF-23: the safety dump's readability check lives in the helper (-VerifyReadable).
        $t.Replace(' -VerifyReadable -Log $restoreLogLine', ' -Log $restoreLogLine')
    } -GoldenSafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 0 -SafetyListExit 6
        $r = Invoke-RestoreScript -ScriptPath $ScriptPath -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $stub
        return ($r.Error -and $r.Error.Exception.Message -match 'Safety dump is unreadable' -and -not $r.CleanRan)
    } -MutantUnsafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 0 -SafetyListExit 6
        $r = Invoke-RestoreScript -ScriptPath $ScriptPath -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $stub
        return $r.CleanRan
    }

    Assert-MutantKilled -Name 'no-input-readable' -SourcePath $goldenRestore -WorkDir $workDir -Transform {
        param($t)
        $t -replace '(?ms)try \{\r?\n\s*Test-PostgresDumpReadable -DumpPath \$DumpPath\r?\n\}\r?\ncatch \{\r?\n\s*Write-Error "Input dump is unreadable[^"]*"\r?\n\s*exit 1\r?\n\}\r?\n\r?\n', ''
    } -GoldenSafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 0 -SecondListExit 7
        $r = Invoke-RestoreScript -ScriptPath $ScriptPath -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $stub
        return ($r.Error -and $r.Error.Exception.Message -match 'Input dump is unreadable' -and -not $r.CleanRan)
    } -MutantUnsafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 0 -SecondListExit 7
        $r = Invoke-RestoreScript -ScriptPath $ScriptPath -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $stub
        return $r.CleanRan
    }

    Assert-MutantKilled -Name 'no-safety-pgdump-exit' -SourcePath $goldenRestore -WorkDir $workDir -Transform {
        param($t)
        $t.Replace('if (-not $safety.Ok) {', 'if ($false) {')
    } -GoldenSafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 4 -PgDumpWriteOnFailure
        $r = Invoke-RestoreScript -ScriptPath $ScriptPath -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $stub
        return ($r.Error -and $r.Error.Exception.Message -match 'Safety pg_dump failed with exit code 4' -and -not $r.CleanRan)
    } -MutantUnsafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 4 -PgDumpWriteOnFailure
        $r = Invoke-RestoreScript -ScriptPath $ScriptPath -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $stub
        return $r.CleanRan
    }

    Assert-MutantKilled -Name 'no-restore-exit' -SourcePath $goldenRestore -WorkDir $workDir -Transform {
        param($t)
        $t.Replace('if ($restoreExit -ne 0) {', 'if ($false) {')
    } -GoldenSafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 0 -SecondListExit 0 -RestoreExit 9
        $r = Invoke-RestoreScript -ScriptPath $ScriptPath -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $stub
        return ($r.Error -and $r.Error.Exception.Message -match 'pg_restore failed with exit code 9')
    } -MutantUnsafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 0 -SecondListExit 0 -RestoreExit 9
        $r = Invoke-RestoreScript -ScriptPath $ScriptPath -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $stub
        return -not $r.Error
    }

    # R10h mutants (brief): (a) recreate BEFORE the safety dump is verified, (b) no recreate (old --clean restore).
    $recreateBlockRe = '(?ms)^\$psql = Get-CheckpointPgExecutable -Name "psql"\r?\n.*?^\}\r?\n(?=\$pgRestore = )'
    Assert-MutantKilled -Name 'recreate-before-safety-verified' -SourcePath $goldenRestore -WorkDir $workDir -Transform {
        param($t)
        $m = [regex]::Match($t, $recreateBlockRe)
        if (-not $m.Success) { return $t }
        $without = $t.Remove($m.Index, $m.Length)
        $anchor = '$safety = Invoke-CheckpointPgDumpNewFile'
        $i = $without.IndexOf($anchor)
        if ($i -lt 0) { return $t }
        $without.Insert($i, $m.Value)
    } -GoldenSafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 0 -SafetyListExit 6
        $r = Invoke-RestoreScript -ScriptPath $ScriptPath -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $stub
        return ($r.Error -and $r.Order -eq 'DUMP,LIST' -and -not $r.CleanRan)
    } -MutantUnsafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 0 -SafetyListExit 6
        $r = Invoke-RestoreScript -ScriptPath $ScriptPath -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $stub
        return ($r.Order -match '^RECREATE,DUMP')
    }
    Assert-MutantKilled -Name 'no-recreate' -SourcePath $goldenRestore -WorkDir $workDir -Transform {
        param($t)
        ([regex]::Replace($t, $recreateBlockRe, '')).Replace('& $pgRestore -h $cfg.Host', '& $pgRestore --clean --if-exists -h $cfg.Host')
    } -GoldenSafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 0 -SecondListExit 0
        $r = Invoke-RestoreScript -ScriptPath $ScriptPath -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $stub
        return (-not $r.Error -and $r.Order -eq 'DUMP,LIST,LIST,RECREATE,RESTORE')
    } -MutantUnsafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 0 -SecondListExit 0
        $r = Invoke-RestoreScript -ScriptPath $ScriptPath -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $stub
        return (-not $r.Error -and $r.Order -eq 'DUMP,LIST,LIST,RESTORE_CLEAN')
    }

    $daily = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\tools\backup-daily.ps1") -Raw
    if ($daily -notmatch "backup-secondary-missing\.flag") {
        throw "backup-daily must write backup-secondary-missing.flag at runtime"
    }
    if ($daily -notmatch "backup-low-disk\.flag") {
        throw "backup-daily must write backup-low-disk.flag when free space < 5GB"
    }
    if ($daily -notmatch "MinFreeBytes") {
        throw "backup-daily must expose MinFreeBytes parameter"
    }
    $pd = Join-Path $workDir "ProgramData\Checkpoint"
    # BL-1/DEF-16: backup-daily writes the flag into -LogsDir, i.e. $pd\logs (not $workDir\logs).
    $lowFlag = Join-Path $pd "logs\backup-low-disk.flag"
    New-Item -ItemType Directory -Force -Path (Join-Path $pd "backups"), (Join-Path $pd "logs") | Out-Null
    $env:ProgramData = $pd
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "..\tools\backup-daily.ps1") `
        -PrimaryBackupDir (Join-Path $pd "backups") -LogsDir (Join-Path $pd "logs") `
        -ServerJsonPath $jsonPath -MinFreeBytes ([long]::MaxValue) 2>&1 | Out-Null
    if (-not (Test-Path -LiteralPath $lowFlag)) {
        throw "backup-daily must write low-disk flag when MinFreeBytes exceeds available space"
    }
    # R10f item 10 (rr4): a missing logs folder is created before the low-disk flag is written.
    $missingLogs = Join-Path $pd "logs-missing-r10f"
    if (Test-Path -LiteralPath $missingLogs) { throw "test setup: $missingLogs must not exist" }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "..\tools\backup-daily.ps1") `
        -PrimaryBackupDir (Join-Path $pd "backups") -LogsDir $missingLogs `
        -ServerJsonPath $jsonPath -MinFreeBytes ([long]::MaxValue) 2>&1 | Out-Null
    $missingFlag = Join-Path $missingLogs "backup-low-disk.flag"
    if (-not (Test-Path -LiteralPath $missingFlag)) {
        throw "backup-daily must create a missing -LogsDir and write the low-disk flag there"
    }
    if ((Get-Content -LiteralPath (Join-Path $missingLogs "backup.log") -Raw) -notmatch 'low disk flag written freeBytes=\d+') {
        throw "backup-daily must log the low-disk flag into the created -LogsDir"
    }

    Write-Host "BackupRestoreConfig: ok"
} finally {
    foreach ($stubDir in $script:StubDirs) {
        Remove-Item -LiteralPath $stubDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}