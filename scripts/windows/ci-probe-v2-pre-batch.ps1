param(
    [Parameter(Mandatory = $true)][int]$Batch
)

$ErrorActionPreference = "Continue"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$artifactDir = if ($env:GITHUB_WORKSPACE) { Join-Path $env:GITHUB_WORKSPACE "artifacts" } else { Join-Path $repoRoot "artifacts" }
$registryLog = Join-Path $artifactDir "kiosk-windows-logs/live-server-registry.log"

$utc = [datetime]::UtcNow.ToString("o")
Write-Host "PROBE_V2_PRE_BATCH utc=$utc batch=$Batch"

Write-Host "--- live-server-registry.log (tail) ---"
if (Test-Path $registryLog) {
    Get-Content $registryLog -Tail 80
} else {
    Write-Host "(not found at $registryLog)"
}

Write-Host "--- TCP listeners on port 5225 ---"
try {
    Get-NetTCPConnection -LocalPort 5225 -ErrorAction SilentlyContinue |
        Select-Object LocalAddress, LocalPort, State, OwningProcess |
        Format-Table -AutoSize
    Get-NetTCPConnection -LocalPort 5225 -ErrorAction SilentlyContinue | ForEach-Object {
        $proc = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
        if ($proc) {
            Write-Host "pid=$($_.OwningProcess) name=$($proc.ProcessName) path=$($proc.Path)"
        }
    }
} catch {
    netstat -ano | Select-String ":5225"
}

Write-Host "--- GameClub.* processes ---"
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -like "GameClub.*" -or ($_.CommandLine -and $_.CommandLine -like "*GameClub.*")
} | ForEach-Object {
    Write-Host "pid=$($_.ProcessId) name=$($_.Name) cmd=$($_.CommandLine)"
}

exit 0