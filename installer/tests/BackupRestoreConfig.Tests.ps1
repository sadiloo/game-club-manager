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
echo %args% | findstr /I "--list" >nul
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
echo %args% | findstr /I "--clean" >nul
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

    $stubBackupEmpty = New-StubPgBinaries -PgDumpExit 0 -PgDumpWriteZeroBytes
    $err = Invoke-BackupScript -ScriptPath $goldenBackup -BackupDir (Join-Path $workDir "backups-empty") -ServerJsonPath $jsonPath -LogsDir (Join-Path $workDir "logs-empty") -Stub $stubBackupEmpty
    if (-not $err) { Write-Error "expected zero-byte pg_dump output to abort backup" }
    if ($err.Exception.Message -notmatch '^Dump file is empty:') {
        Write-Error "expected Dump file is empty message; got $($err.Exception.Message)"
    }

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
        $t -replace '(?ms)& pg_dump[^\r\n]+\r?\nif \(\$LASTEXITCODE -ne 0\) \{\r?\n\s*Write-Error "Safety pg_dump failed[^"]*"\r?\n\s*exit 1\r?\n\}\r?\n\r?\n', "& pg_dump -Fc -h `$cfg.Host -p `$cfg.Port -U `$cfg.Username `$cfg.Database -f `$pre`n"
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
        $t -replace '(?ms)& pg_restore --clean[^\r\n]+\r?\nif \(\$LASTEXITCODE -ne 0\) \{\r?\n\s*Write-Error "pg_restore failed[^"]*"\r?\n\s*exit 1\r?\n\}\r?\n\r?\n', "& pg_restore --clean --if-exists -h `$cfg.Host -p `$cfg.Port -U `$cfg.Username -d `$cfg.Database `$DumpPath`n"
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

    Write-Host "BackupRestoreConfig: ok"
} finally {
    foreach ($stubDir in $script:StubDirs) {
        Remove-Item -LiteralPath $stubDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}