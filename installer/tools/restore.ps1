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
$safety = Invoke-CheckpointPgDumpNewFile -PgDump $pgDump -PgArgs @("-Fc", "-h", $cfg.Host, "-p", $cfg.Port, "-U", $cfg.Username, "-d", $cfg.Database) `
    -OutFile $pre -Label "restore-safety" -VerifyReadable -Log {
        param([string]$line)
        Write-Host $line
        Add-Content -LiteralPath $restoreLog -Value ("{0:o} {1}" -f (Get-Date), $line)
    }
if (-not $safety.Ok) {
    if ($safety.Unreadable) {
        Write-Error "Safety dump is unreadable ($($safety.Detail)); restore aborted before --clean."
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
    Write-Error "Input dump is unreadable ($($_.Exception.Message)); restore aborted before --clean."
    exit 1
}

Write-Host "خدمت سرور متوقف می‌شود …"
Stop-Service CheckpointServer -ErrorAction SilentlyContinue

Write-Host "بازیابی از $DumpPath …"
$pgRestore = Get-CheckpointPgExecutable -Name "pg_restore"
& $pgRestore --clean --if-exists -h $cfg.Host -p $cfg.Port -U $cfg.Username -d $cfg.Database $DumpPath
if ($LASTEXITCODE -ne 0) {
    Write-Error "pg_restore failed with exit code $LASTEXITCODE"
    exit 1
}

Write-Host "سرور دوباره روشن می‌شود."
Start-Service CheckpointServer -ErrorAction SilentlyContinue
Write-Host "تمام شد."
Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue