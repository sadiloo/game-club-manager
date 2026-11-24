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

$paths = @(
    "C:\ProgramData\Checkpoint\config\server.json",
    "C:\ProgramData\Checkpoint\config\.install-secrets.json"
)
foreach ($p in $paths) {
    if (Test-Path -LiteralPath $p) {
        Remove-Item -LiteralPath $p -Force
    }
}

Write-Host "Reset-InstallerAcceptanceMachine: ok"