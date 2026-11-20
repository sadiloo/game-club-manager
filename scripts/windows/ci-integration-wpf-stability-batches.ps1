$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot

$batches = 5
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

$passed = 0
$scriptPath = Join-Path $PSScriptRoot "ci-integration-wpf-stability.ps1"
for ($batch = 1; $batch -le $batches; $batch++) {
    Write-Host "WPF stability batch $batch/$batches ($runsPerBatch runs each)"
    $env:WPF_STABILITY_RUNS = "$runsPerBatch"
    & $scriptPath
    if ($LASTEXITCODE -ne 0) {
        Write-Host "WPF_STABILITY_BATCH_FAILED batch=$batch passed_so_far=$passed total_target=$($batches * $runsPerBatch)"
        exit $LASTEXITCODE
    }
    $passed += $runsPerBatch
    Write-Host "WPF_STABILITY_BATCH_OK batch=$batch cumulative=$passed/$($batches * $runsPerBatch)"
}

Write-Host "WPF_STABILITY_ALL_BATCHES_OK $passed/$($batches * $runsPerBatch)"
exit 0