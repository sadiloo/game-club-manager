# CI only: remove partial Checkpoint installs so B-1/B-2 start from a consistent state.
$ErrorActionPreference = "Stop"

$uninstaller = "C:\Program Files\Checkpoint\Server\unins000.exe"
if (Test-Path -LiteralPath $uninstaller) {
    $proc = Start-Process -FilePath $uninstaller -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        Write-Host "Reset-InstallerAcceptanceMachine: uninstall exit $($proc.ExitCode)"
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

$pgdata = "C:\ProgramData\Checkpoint\pgdata"
if (Test-Path -LiteralPath $pgdata) {
    Remove-Item -LiteralPath $pgdata -Recurse -Force
}

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