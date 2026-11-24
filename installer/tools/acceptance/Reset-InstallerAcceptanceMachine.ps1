# CI only: remove partial Checkpoint installs so B-1/B-2 start from a consistent state.
$ErrorActionPreference = "Stop"

foreach ($svc in @("CheckpointServer", "CheckpointPostgres")) {
    $s = Get-Service $svc -ErrorAction SilentlyContinue
    if ($s) {
        if ($s.Status -eq "Running") {
            Stop-Service $svc -Force -ErrorAction SilentlyContinue
        }
        & sc.exe delete $svc 2>&1 | Out-Null
    }
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