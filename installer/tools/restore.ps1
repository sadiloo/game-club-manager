# Checkpoint server restore helper (B). Persian guidance on console; no UI.
param(
    [Parameter(Mandatory)][string]$DumpPath,
    [string]$ServerJsonPath = "$env:ProgramData\Checkpoint\config\server.json"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "CheckpointDbConfig.ps1")

if (-not (Test-Path -LiteralPath $DumpPath)) {
    Write-Error "Dump file not found: $DumpPath"
    exit 1
}

$cfg = Read-CheckpointServerJsonConfig -ServerJsonPath $ServerJsonPath
$safeDir = Join-Path $env:ProgramData "Checkpoint\backups"
if (-not (Test-Path -LiteralPath $safeDir)) {
    New-Item -ItemType Directory -Force -Path $safeDir | Out-Null
}

Write-Host "پشتیبان امنیتی قبل از بازیابی …"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$pre = Get-CheckpointNewDumpPath -Directory $safeDir -Stem "pre-restore-$stamp"
$restoreLogDir = Join-Path $env:ProgramData "Checkpoint\logs"
if (-not (Test-Path -LiteralPath $restoreLogDir)) { New-Item -ItemType Directory -Force -Path $restoreLogDir | Out-Null }
$restoreLog = Join-Path $restoreLogDir "restore.log"

$env:PGPASSWORD = $cfg.Password
$pgDump = Get-CheckpointPgExecutable -Name "pg_dump"
# DEF-20: the safety dump goes to a NEW file; if pg_dump fails or writes 0 bytes, only that file is removed (logged).
# DEF-23: the same holds when pg_dump exits 0 but its output fails `pg_restore --list` (-VerifyReadable). Older dumps
# are never touched.
$restoreLogLine = {
    param([string]$line)
    Write-Host $line
    Add-Content -LiteralPath $restoreLog -Value ("{0:o} {1}" -f (Get-Date), $line)
}
$safety = Invoke-CheckpointPgDumpNewFile -PgDump $pgDump -PgArgs @("-Fc", "-h", $cfg.Host, "-p", $cfg.Port, "-U", $cfg.Username, "-d", $cfg.Database) `
    -OutFile $pre -Label "restore-safety" -VerifyReadable -Log $restoreLogLine
if (-not $safety.Ok) {
    if ($safety.Unreadable) {
        Write-Error "Safety dump is unreadable ($($safety.Detail)); restore aborted, the database was not touched."
    } elseif ($safety.Empty) {
        Write-Error "Safety pg_dump wrote an empty file; restore aborted."
    } else {
        Write-Error "Safety pg_dump failed with exit code $($safety.ExitCode); restore aborted."
    }
    exit 1
}

try {
    Test-PostgresDumpReadable -DumpPath $DumpPath
}
catch {
    Write-Error "Input dump is unreadable ($($_.Exception.Message)); restore aborted, the database was not touched."
    exit 1
}

Write-Host "خدمت سرور متوقف می‌شود …"
Stop-Service CheckpointServer -ErrorAction SilentlyContinue

# R10h (BL-rr6-2): `pg_restore --clean` only drops what the dump itself contains, so tables and migration history that
# a NEWER version created would survive a restore of an older dump and the next upgrade would fail (42P07). Only now,
# after the safety dump above was written AND verified readable and the input dump was verified readable, the public
# schema is dropped and recreated (with the PostgreSQL 15+ default owner and grant), and the dump is restored into the
# empty schema without --clean. Both steps are logged.
Write-Host "بازیابی از $DumpPath …"
$psql = Get-CheckpointPgExecutable -Name "psql"
& $restoreLogLine "CHECKPOINT_RESTORE_SCHEMA_RECREATE database=$($cfg.Database) safety_dump=$pre input_dump=$DumpPath action=drop_and_create_public_schema"
& $psql -h $cfg.Host -p $cfg.Port -U $cfg.Username -d $cfg.Database -v ON_ERROR_STOP=1 -c "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public AUTHORIZATION pg_database_owner; GRANT USAGE ON SCHEMA public TO PUBLIC;"
$recreateExit = $LASTEXITCODE
if ($recreateExit -ne 0) {
    & $restoreLogLine "CHECKPOINT_RESTORE_SCHEMA_RECREATE_FAILED exit=$recreateExit database=$($cfg.Database) safety_dump=$pre"
    Write-Error "Recreating the database schema failed with exit code $recreateExit; restore aborted. Safety dump: $pre"
    exit 1
}
$pgRestore = Get-CheckpointPgExecutable -Name "pg_restore"
& $pgRestore -h $cfg.Host -p $cfg.Port -U $cfg.Username -d $cfg.Database $DumpPath
$restoreExit = $LASTEXITCODE
if ($restoreExit -ne 0) {
    & $restoreLogLine "CHECKPOINT_RESTORE_FAILED exit=$restoreExit database=$($cfg.Database) safety_dump=$pre recovery=restore.ps1 -DumpPath $pre"
    Write-Error "pg_restore failed with exit code $restoreExit. The schema was recreated; to go back, run restore.ps1 -DumpPath `"$pre`""
    exit 1
}
& $restoreLogLine "CHECKPOINT_RESTORE_DONE database=$($cfg.Database) input_dump=$DumpPath safety_dump=$pre"

Write-Host "سرور دوباره روشن می‌شود."
Start-Service CheckpointServer -ErrorAction SilentlyContinue
Write-Host "تمام شد."
Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue