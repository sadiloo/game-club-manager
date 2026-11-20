$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot

$batches = 10
if ($env:WPF_STABILITY_BATCHES -match '^\d+$') {
    $batches = [int]$env:WPF_STABILITY_BATCHES
}
if ($batches -le 0) {
    Write-Host "WPF stability batches disabled (WPF_STABILITY_BATCHES=$batches)"
    exit 0
}

$runsPerBatch = 20
if ($env:WPF_STABILITY_RUNS -match '^\d+$') {
    $runsPerBatch = [int]$env:WPF_STABILITY_RUNS
}

$artifactDir = Join-Path $env:GITHUB_WORKSPACE "artifacts"
if ([string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE)) {
    $artifactDir = Join-Path $repoRoot "artifacts"
}
$logDir = Join-Path $artifactDir "kiosk-windows-logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$redRunsFile = Join-Path $logDir "wpf-stability-red-runs.jsonl"
if (Test-Path $redRunsFile) { Remove-Item $redRunsFile -Force }

$scriptPath = Join-Path $PSScriptRoot "ci-integration-wpf-stability.ps1"
$totalTarget = $batches * $runsPerBatch

for ($batch = 1; $batch -le $batches; $batch++) {
    Write-Host "WPF stability batch $batch/$batches ($runsPerBatch runs each)"
    $env:WPF_STABILITY_RUNS = "$runsPerBatch"
    $env:WPF_STABILITY_BATCH_INDEX = "$batch"
    $env:WPF_STABILITY_GLOBAL_OFFSET = ($batch - 1) * $runsPerBatch
    & $scriptPath
    $global:LASTEXITCODE = 0
}

$redRuns = [System.Collections.Generic.List[int]]::new()
if (Test-Path $redRunsFile) {
    Get-Content $redRunsFile | ForEach-Object {
        try {
            $obj = $_ | ConvertFrom-Json
            $redRuns.Add([int]$obj.run) | Out-Null
        } catch { }
    }
}

$redList = ($redRuns | Sort-Object) -join ","
Write-Host "STABILITY_SUMMARY total=$totalTarget reds=$($redRuns.Count) red_runs=[$redList]"

if ($redRuns.Count -gt 0) {
    exit 1
}
Write-Host "WPF_STABILITY_ALL_BATCHES_OK $totalTarget/$totalTarget"
exit 0