# CI only: remove partial Checkpoint installs so B-1/B-2 start from a consistent state.
$ErrorActionPreference = "Stop"

$uninstaller = "C:\Program Files\Checkpoint\Server\unins000.exe"
if (Test-Path -LiteralPath $uninstaller) {
    $proc = Start-Process -FilePath $uninstaller -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        throw "Reset-InstallerAcceptanceMachine: uninstall exit $($proc.ExitCode)"
    }
}

foreach ($keyPath in @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8E5E6B2E-6B6D-4F5B-9D9A-4F4E7F8F6B6D}_is1",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{8E5E6B2E-6B6D-4F5B-9D9A-4F4E7F8F6B6D}_is1"
    )) {
    if (Test-Path -LiteralPath $keyPath) {
        Remove-Item -LiteralPath $keyPath -Recurse -Force
    }
}

foreach ($dir in @(
        "C:\Program Files\Checkpoint\Server",
        "C:\Program Files\Checkpoint\PostgreSQL"
    )) {
    if (Test-Path -LiteralPath $dir) {
        Remove-Item -LiteralPath $dir -Recurse -Force
    }
}

foreach ($svc in @("CheckpointServer", "CheckpointPostgres")) {
    $s = Get-Service $svc -ErrorAction SilentlyContinue
    if ($s) {
        if ($s.Status -eq "Running") {
            Stop-Service $svc -Force -ErrorAction SilentlyContinue
        }
        & sc.exe delete $svc 2>&1 | Out-Null
    }
}

& schtasks.exe /Delete /TN CheckpointDailyBackup /F 2>$null | Out-Null
# R10f item 7: sc delete / schtasks /Delete are best effort (the service or task may not exist on a fresh runner). Their
# non-zero exit is expected and consumed here, so the leg ledger's "no unchecked native exit" rule stays meaningful.
$global:LASTEXITCODE = 0

$staleFailPhase = Join-Path "C:\ProgramData\Checkpoint\config" ".internal-test-fail-phase"
if (Test-Path -LiteralPath $staleFailPhase) {
    Remove-Item -LiteralPath $staleFailPhase -Force
}

foreach ($dir in @("pgdata", "backups", "config")) {
    $p = Join-Path "C:\ProgramData\Checkpoint" $dir
    if (Test-Path -LiteralPath $p) {
        Remove-Item -LiteralPath $p -Recurse -Force
    }
}

$pgData = "C:\ProgramData\Checkpoint\pgdata"
if (Test-Path -LiteralPath $pgData) {
    throw "Reset-InstallerAcceptanceMachine: pgdata still present after cleanup"
}

Write-Host "Reset-InstallerAcceptanceMachine: ok"