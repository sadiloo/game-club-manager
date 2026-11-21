$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $root "installer\tools\CheckpointDbConfig.ps1")

function New-StubPgBinaries {
    param(
        [int]$PgDumpExit = 0,
        [int]$PgRestoreListExit = 0,
        [int]$PgRestoreCleanExit = 0,
        [string]$PgDumpOutFile = "",
        [int]$PgRestoreListExitAfterSafety = -1
    )
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("gcm-stub-pg-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $trace = Join-Path $dir "pg-restore-trace.txt"
    $listPass = Join-Path $dir "pg_restore-list-passed"

    $dumpOut = $PgDumpOutFile.Replace("'", "'\\''")
    $dumpScript = @"
#!/bin/sh
outfile='$dumpOut'
while [ `$# -gt 0 ]; do
  if [ "`$1" = "-f" ]; then outfile="`$2"; shift 2; continue; fi
  shift
done
if [ -n "`$outfile" ]; then
  printf '\001' > "`$outfile"
fi
exit $PgDumpExit
"@
    Set-Content -LiteralPath (Join-Path $dir "pg_dump") -Value $dumpScript -Encoding UTF8 -NoNewline
    [System.IO.File]::AppendAllText((Join-Path $dir "pg_dump"), "`n")

    $listAfter = if ($PgRestoreListExitAfterSafety -ge 0) { $PgRestoreListExitAfterSafety } else { $PgRestoreListExit }
    $restoreScript = @"
#!/bin/sh
for a in "`$@"; do
  case "`$a" in
    --list)
      if [ -f '$listPass' ]; then
        exit $listAfter
      fi
      touch '$listPass'
      exit $PgRestoreListExit
      ;;
    --clean)
      echo CLEAN >> '$trace'
      exit $PgRestoreCleanExit
      ;;
  esac
done
exit 0
"@
    Set-Content -LiteralPath (Join-Path $dir "pg_restore") -Value $restoreScript -Encoding UTF8 -NoNewline
    [System.IO.File]::AppendAllText((Join-Path $dir "pg_restore"), "`n")

    foreach ($name in @('pg_dump', 'pg_restore')) {
        & chmod +x (Join-Path $dir $name)
    }

    return [pscustomobject]@{ Dir = $dir; Trace = $trace }
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
    }
    catch [System.Management.Automation.RuntimeException] {
        if ($_.Exception.Message -notmatch '^Dump file is empty:') {
            throw
        }
        $emptyThrown = $true
    }
    if (-not $emptyThrown) {
        Write-Error "expected empty dump to throw Dump file is empty"
    }

    # Mutant check: a no-op Test-PostgresDumpReadable must not satisfy the empty-dump guard above.
    function Test-PostgresDumpReadable_NoOp { param([string]$DumpPath) }
    $mutantPassesEmpty = $true
    try {
        Test-PostgresDumpReadable_NoOp -DumpPath $emptyDump
    } catch {
        $mutantPassesEmpty = $false
    }
    if (-not $mutantPassesEmpty) {
        Write-Error "mutant harness broken: no-op reader must accept a zero-byte dump (so replacing Test-PostgresDumpReadable would fail the empty-dump test)"
    }

    $backupDir = Join-Path $workDir "backups"
    $backupScript = Join-Path $root "installer\tools\backup-daily.ps1"
  $stubGood = New-StubPgBinaries -PgDumpExit 0 -PgRestoreListExit 0 -PgDumpOutFile (Join-Path $backupDir "out.dump")
  try {
        $env:PATH = "$($stubGood.Dir)" + [IO.Path]::PathSeparator + $env:PATH
        $env:ProgramData = $workDir
        New-Item -ItemType Directory -Force -Path (Join-Path $workDir "Checkpoint\config") | Out-Null
        Copy-Item -LiteralPath $jsonPath -Destination (Join-Path $workDir "Checkpoint\config\server.json")
        & $backupScript -PrimaryBackupDir $backupDir -ServerJsonPath (Join-Path $workDir "Checkpoint\config\server.json") -LogsDir (Join-Path $workDir "logs")
        if (-not (Test-Path -LiteralPath $backupDir)) {
            Write-Error "backup dir was not created"
        }
    }
    finally {
        $env:PATH = ($env:PATH -split [IO.Path]::PathSeparator | Where-Object { $_ -ne $stubGood.Dir }) -join [IO.Path]::PathSeparator
    }

    $stubDumpFail = New-StubPgBinaries -PgDumpExit 3
    try {
        $env:PATH = "$($stubDumpFail.Dir)" + [IO.Path]::PathSeparator + $env:PATH
        $failed = $false
        try {
            & $backupScript -PrimaryBackupDir (Join-Path $workDir "backups-fail") -ServerJsonPath $jsonPath -LogsDir (Join-Path $workDir "logs2")
        } catch {
            if ($_.Exception.Message -notmatch 'pg_dump failed with exit code 3') { throw }
            $failed = $true
        }
        if (-not $failed) { Write-Error "expected pg_dump failure to abort backup" }
    }
    finally {
        $env:PATH = ($env:PATH -split [IO.Path]::PathSeparator | Where-Object { $_ -ne $stubDumpFail.Dir }) -join [IO.Path]::PathSeparator
    }

    $restoreScript = Join-Path $root "installer\tools\restore.ps1"
    $inputDump = Join-Path $workDir "input.dump"
    [System.IO.File]::WriteAllBytes($inputDump, [byte[]](1, 2, 3))
    $safeDir = Join-Path $workDir "Checkpoint\backups"
    New-Item -ItemType Directory -Force -Path $safeDir | Out-Null
    $env:ProgramData = $workDir

    $stubSafetyUnreadable = New-StubPgBinaries -PgDumpExit 0 -PgRestoreListExit 5 -PgDumpOutFile (Join-Path $safeDir "pre.dump")
    try {
        $env:PATH = "$($stubSafetyUnreadable.Dir)" + [IO.Path]::PathSeparator + $env:PATH
        $safetyAborted = $false
        try {
            & $restoreScript -DumpPath $inputDump -ServerJsonPath $jsonPath 2>&1 | Out-Null
        } catch {
            if ($_.Exception.Message -notmatch 'restore aborted before --clean') { throw }
            $safetyAborted = $true
        }
        if (-not $safetyAborted) { Write-Error "expected safety dump list failure to abort restore" }
        if (Test-Path -LiteralPath $stubSafetyUnreadable.Trace) {
            $trace = Get-Content -LiteralPath $stubSafetyUnreadable.Trace -Raw
            if ($trace -match 'CLEAN') { Write-Error "restore must not run --clean when safety dump is unreadable" }
        }
    }
    finally {
        $env:PATH = ($env:PATH -split [IO.Path]::PathSeparator | Where-Object { $_ -ne $stubSafetyUnreadable.Dir }) -join [IO.Path]::PathSeparator
    }

    $stubInputUnreadable = New-StubPgBinaries -PgDumpExit 0 -PgRestoreListExit 0
    try {
        $env:PATH = "$($stubInputUnreadable.Dir)" + [IO.Path]::PathSeparator + $env:PATH
        @"
#!/bin/sh
list='$($stubInputUnreadable.Dir)/calls'
for a in "`$@"; do
  case "`$a" in
    --list)
      if [ ! -f "`$list" ]; then
        touch "`$list"
        exit 7
      fi
      exit 0
      ;;
    --clean)
      echo CLEAN >> '$($stubInputUnreadable.Trace)'
      exit 0
      ;;
  esac
done
exit 0
"@ | Set-Content -LiteralPath (Join-Path $stubInputUnreadable.Dir "pg_restore") -Encoding UTF8
        & chmod +x (Join-Path $stubInputUnreadable.Dir "pg_restore")

        $inputAborted = $false
        try {
            & $restoreScript -DumpPath $inputDump -ServerJsonPath $jsonPath 2>&1 | Out-Null
        } catch {
            if ($_.Exception.Message -notmatch 'restore aborted before --clean') { throw }
            $inputAborted = $true
        }
        if (-not $inputAborted) { Write-Error "expected input dump validation failure to abort restore" }
        if (Test-Path -LiteralPath $stubInputUnreadable.Trace) {
            $trace = Get-Content -LiteralPath $stubInputUnreadable.Trace -Raw
            if ($trace -match 'CLEAN') { Write-Error "restore must validate input dump before --clean" }
        }
    }
    finally {
        $env:PATH = ($env:PATH -split [IO.Path]::PathSeparator | Where-Object { $_ -ne $stubInputUnreadable.Dir }) -join [IO.Path]::PathSeparator
    }

    $stubRestoreFail = New-StubPgBinaries -PgDumpExit 0 -PgRestoreListExit 0 -PgRestoreCleanExit 9
    try {
        $env:PATH = "$($stubRestoreFail.Dir)" + [IO.Path]::PathSeparator + $env:PATH
        $raised = $false
        try {
            & $restoreScript -DumpPath $inputDump -ServerJsonPath $jsonPath 2>&1 | Out-Null
        } catch {
            $raised = $true
        }
        if (-not $raised -and $LASTEXITCODE -ne 9) {
            Write-Error "expected pg_restore --clean failure to surface non-zero exit"
        }
    }
    finally {
        $env:PATH = ($env:PATH -split [IO.Path]::PathSeparator | Where-Object { $_ -ne $stubRestoreFail.Dir }) -join [IO.Path]::PathSeparator
    }

    Write-Host "BackupRestoreConfig: ok"
}
finally {
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}