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
        [int]$CleanExit = 0,
        [switch]$PgDumpWriteOnFailure,
        [switch]$PgDumpWriteZeroBytes
    )
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("gcm-stub-pg-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $script:StubDirs.Add($dir) | Out-Null
    $listTrace = Join-Path $dir "list-trace.txt"
    $cleanTrace = Join-Path $dir "clean-trace.txt"
    $listPass = Join-Path $dir "list.pass"

    $writeOnFail = if ($PgDumpWriteOnFailure) { "1" } else { "0" }
    $writeZero = if ($PgDumpWriteZeroBytes) { "1" } else { "0" }
    if ($IsWindows -or ($env:OS -eq 'Windows_NT')) {
        @"
@echo off
setlocal EnableDelayedExpansion
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
set "args=%*"
echo %args% | findstr /I /C:"--list" >nul
if %errorlevel% equ 0 (
  if not exist "$listPass" (
    echo LIST>> "$listTrace"
    echo.>"$listPass"
    exit /b $FirstListExit
  ) else (
    echo LIST>> "$listTrace"
    exit /b $SecondListExit
  )
)
echo %args% | findstr /I /C:"--clean" >nul
if %errorlevel% equ 0 (
  echo CLEAN>> "$cleanTrace"
  exit /b $CleanExit
)
exit /b 0
"@ | Set-Content -LiteralPath (Join-Path $dir "pg_restore.cmd") -Encoding ASCII
    } else {
        @"
#!/bin/sh
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
for a in "`$@"; do
  case "`$a" in
    --list)
      if [ ! -f '$listPass' ]; then
        echo LIST >> '$listTrace'
        touch '$listPass'
        exit $FirstListExit
      fi
      echo LIST >> '$listTrace'
      exit $SecondListExit
      ;;
    --clean)
      echo CLEAN >> '$cleanTrace'
      exit $CleanExit
      ;;
  esac
done
exit 0
"@ | Set-Content -LiteralPath (Join-Path $dir "pg_restore") -Encoding UTF8 -NoNewline
        [System.IO.File]::AppendAllText((Join-Path $dir "pg_restore"), "`n")
        foreach ($name in @('pg_dump', 'pg_restore')) {
            & chmod +x (Join-Path $dir $name)
        }
    }

    return [pscustomobject]@{
        Dir        = $dir
        ListTrace  = $listTrace
        CleanTrace = $cleanTrace
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
            $clean = ''
            $list = ''
            if (Test-Path -LiteralPath $Stub.CleanTrace) { $clean = Get-Content -LiteralPath $Stub.CleanTrace -Raw }
            if (Test-Path -LiteralPath $Stub.ListTrace) { $list = Get-Content -LiteralPath $Stub.ListTrace -Raw }
            return [pscustomobject]@{
                Error     = $recorded
                CleanRan  = $clean -match 'CLEAN'
                ListCalls = @($list -split "`n" | Where-Object { $_ -match '\S' }).Count
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
    $dest = Join-Path $WorkDir "mutant-$Tag.ps1"
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
    $mutantSurvives = $false
    try {
        $mutantSurvives = & $MutantUnsafe $mutant
    } catch {
        $mutantSurvives = $false
    }
    if ($mutantSurvives) {
        Write-Error "mutant not killed: $Name"
    }
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
        $stub = New-StubPgBinaries -PgDumpExit 0 -PgDumpWriteZeroBytes
        $err = Invoke-BackupScript -ScriptPath $ScriptPath -BackupDir (Join-Path $workDir "bk-g-old") -ServerJsonPath $jsonPath -LogsDir (Join-Path $workDir "lg-g-old") -Stub $stub
        return ($null -ne $err -and $err.Exception.Message -match '^Dump file is empty:')
    } -MutantUnsafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 0 -PgDumpWriteZeroBytes
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
    if ($r.CleanRan) { Write-Error "restore must not reach --clean when safety pg_dump fails" }

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
        if ($r.CleanRan) { Write-Error "DEF-20 $($case.Name): restore must not reach --clean" }
        $left = @(Get-ChildItem -LiteralPath $bkDir -File | ForEach-Object { $_.Name } | Sort-Object)
        $expect = @("checkpoint-20250101-000000.000.dump", "pre-restore-20250101-000000.dump")
        if (($left -join ",") -ne ($expect -join ",")) { Write-Error "DEF-20 $($case.Name): only the failed safety dump may be removed; left=[$($left -join ',')]" }
        if ((([System.IO.File]::ReadAllBytes($goodOld)) -join ",") -ne "7,7,7" -or (([System.IO.File]::ReadAllBytes($goodCp)) -join ",") -ne "8,8,8,8") { Write-Error "DEF-20 $($case.Name): earlier backups changed" }
        $logText = if (Test-Path -LiteralPath $restoreLogPath) { Get-Content -LiteralPath $restoreLogPath -Raw } else { "" }
        if ($logText -notmatch $case.LogRe) { Write-Error "DEF-20 $($case.Name): restore.log lacks /$($case.LogRe)/; got [$logText]" }
    }
    Remove-Item -LiteralPath $goodOld, $goodCp -Force

    $safetyUnreadStub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 6
    $r = Invoke-RestoreScript -ScriptPath $goldenRestore -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $safetyUnreadStub
    if (-not $r.Error) { Write-Error "expected safety dump readability failure" }
    if ($r.Error.Exception.Message -notmatch 'Safety dump is unreadable') {
        Write-Error "unexpected safety readability message: $($r.Error.Exception.Message)"
    }
    if ($r.CleanRan) { Write-Error "restore must not reach --clean when safety dump is unreadable" }

    $inputUnreadStub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 0 -SecondListExit 7
    $r = Invoke-RestoreScript -ScriptPath $goldenRestore -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $inputUnreadStub
    if (-not $r.Error) { Write-Error "expected input dump readability failure" }
    if ($r.Error.Exception.Message -notmatch 'Input dump is unreadable') {
        Write-Error "unexpected input readability message: $($r.Error.Exception.Message)"
    }
    if ($r.ListCalls -lt 2) { Write-Error "expected input validation to run pg_restore --list after safety list" }
    if ($r.CleanRan) { Write-Error "restore must not reach --clean when input dump is unreadable" }

    $cleanFailStub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 0 -SecondListExit 0 -CleanExit 9
    $r = Invoke-RestoreScript -ScriptPath $goldenRestore -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $cleanFailStub
    if (-not $r.Error) { Write-Error "expected pg_restore --clean failure" }
    if ($r.Error.Exception.Message -notmatch 'pg_restore failed with exit code 9') {
        Write-Error "unexpected pg_restore --clean message: $($r.Error.Exception.Message)"
    }
    if (-not $r.CleanRan) { Write-Error "expected stub to record --clean invocation" }

    Assert-MutantKilled -Name 'no-safety-readable' -SourcePath $goldenRestore -WorkDir $workDir -Transform {
        param($t)
        $t -replace '(?ms)try \{\r?\n\s*Test-PostgresDumpReadable -DumpPath \$pre\r?\n\}\r?\ncatch \{\r?\n\s*Write-Error "Safety dump is unreadable[^"]*"\r?\n\s*exit 1\r?\n\}\r?\n\r?\n', ''
    } -GoldenSafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 6
        $r = Invoke-RestoreScript -ScriptPath $ScriptPath -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $stub
        return ($r.Error -and $r.Error.Exception.Message -match 'Safety dump is unreadable' -and -not $r.CleanRan)
    } -MutantUnsafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 6
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

    Assert-MutantKilled -Name 'no-clean-exit' -SourcePath $goldenRestore -WorkDir $workDir -Transform {
        param($t)
        $t -replace '(?ms)\$pgRestore = Get-CheckpointPgExecutable -Name "pg_restore"\r?\n& \$pgRestore[^\r\n]+\r?\nif \(\$LASTEXITCODE -ne 0\) \{\r?\n\s*Write-Error "pg_restore failed[^"]*"\r?\n\s*exit 1\r?\n\}\r?\n\r?\n', "`$pgRestore = Get-CheckpointPgExecutable -Name `"pg_restore`"`n& `$pgRestore --clean --if-exists -h `$cfg.Host -p `$cfg.Port -U `$cfg.Username -d `$cfg.Database `$DumpPath`n"
    } -GoldenSafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 0 -SecondListExit 0 -CleanExit 9
        $r = Invoke-RestoreScript -ScriptPath $ScriptPath -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $stub
        return ($r.Error -and $r.Error.Exception.Message -match 'pg_restore failed with exit code 9')
    } -MutantUnsafe {
        param([string]$ScriptPath)
        $stub = New-StubPgBinaries -PgDumpExit 0 -FirstListExit 0 -SecondListExit 0 -CleanExit 9
        $r = Invoke-RestoreScript -ScriptPath $ScriptPath -DumpPath $inputDump -ServerJsonPath $jsonPath -WorkProgramData $workDir -Stub $stub
        return -not $r.Error
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