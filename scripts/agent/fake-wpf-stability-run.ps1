param(
    [int]$GlobalRun,
    [int]$BatchRun,
    [string]$LoginName
)
# Fake WPF stability iteration for probe CI script validation (Linux/pwsh).
$failGlobal = 2
if ($env:WPF_STABILITY_FAKE_FAIL_GLOBAL_RUN -match '^\d+$') {
    $failGlobal = [int]$env:WPF_STABILITY_FAKE_FAIL_GLOBAL_RUN
}
Write-Host "FAKE_WPF_RUN global=$GlobalRun batchRun=$BatchRun login=$LoginName"
if ($GlobalRun -eq $failGlobal) {
    Write-Host "FAKE_WPF_RUN forced failure on global run $GlobalRun"
    exit 1
}
exit 0