# Checkpoint daily backup (B-3). Dumps are kept forever; this script only writes new ones.
param(
    [string] $PrimaryBackupDir = "C:\ProgramData\Checkpoint\backups",
    [string] $SecondaryBackupDir = "",
    [string] $LogsDir = "C:\ProgramData\Checkpoint\logs",
    [string] $ServerJsonPath = "$env:ProgramData\Checkpoint\config\server.json",
    [long] $MinFreeBytes = 5368709120
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "CheckpointDbConfig.ps1")

$logPath = Join-Path $LogsDir "backup.log"
$secondaryMissingFlag = Join-Path $LogsDir "backup-secondary-missing.flag"
$lowDiskFlag = Join-Path $LogsDir "backup-low-disk.flag"
function Write-BackupLog([string]$line) {
    if (-not (Test-Path -LiteralPath $LogsDir)) {
        New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null
    }
    Add-Content -Path $logPath -Value "$(Get-Date -Format o) $line"
}

try {
    if (-not (Test-Path -LiteralPath $PrimaryBackupDir)) {
        New-Item -ItemType Directory -Force -Path $PrimaryBackupDir | Out-Null
    }

    $driveRoot = [System.IO.Path]::GetPathRoot((Resolve-Path -LiteralPath $PrimaryBackupDir).Path)
    if ($driveRoot) {
        $drive = New-Object System.IO.DriveInfo($driveRoot)
        if ($drive.AvailableFreeSpace -lt $MinFreeBytes) {
            Set-Content -LiteralPath $lowDiskFlag -Value "فضای آزاد دیسک پشتیبان کمتر از ۵ گیگابایت است." -Encoding UTF8
            Write-BackupLog "low disk flag written freeBytes=$($drive.AvailableFreeSpace)"
        } elseif (Test-Path -LiteralPath $lowDiskFlag) {
            Remove-Item -LiteralPath $lowDiskFlag -Force
        }
    }

    $cfg = Read-CheckpointServerJsonConfig -ServerJsonPath $ServerJsonPath
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss.fff"
    $dump = Get-CheckpointNewDumpPath -Directory $PrimaryBackupDir -Stem "checkpoint-$stamp"

    $env:PGPASSWORD = $cfg.Password
    $pgDump = Get-CheckpointPgExecutable -Name "pg_dump"
    $run = Invoke-CheckpointPgDumpNewFile -PgDump $pgDump -OutFile $dump -Label "backup-daily" `
        -PgArgs @("-Fc", "-h", $cfg.Host, "-p", $cfg.Port, "-U", $cfg.Username, "-d", $cfg.Database) `
        -Log { param([string]$line) Write-BackupLog $line }
    if ($run.ExitCode -ne 0) {
        throw "pg_dump failed with exit code $($run.ExitCode)"
    }
    if ($run.Empty) {
        throw "Dump file is empty: $dump"
    }

    Test-PostgresDumpReadable -DumpPath $dump

    if ($SecondaryBackupDir) {
        if (-not (Test-Path -LiteralPath $SecondaryBackupDir)) {
            Set-Content -LiteralPath $secondaryMissingFlag -Value "پوشهٔ دوم پشتیبان در زمان اجرا در دسترس نیست." -Encoding UTF8
            Write-BackupLog "secondary missing at runtime dir=$SecondaryBackupDir"
            throw "Secondary backup folder missing: $SecondaryBackupDir"
        }
        Copy-Item -LiteralPath $dump -Destination (Join-Path $SecondaryBackupDir (Split-Path $dump -Leaf))
        if (Test-Path -LiteralPath $secondaryMissingFlag) {
            Remove-Item -LiteralPath $secondaryMissingFlag -Force
        }
    }

    Write-BackupLog "backup ok $dump"
}
catch {
    Write-BackupLog "backup error $($_.Exception.Message)"
    throw
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}