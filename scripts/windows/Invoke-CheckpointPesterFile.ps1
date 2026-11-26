# Runs Describe/It (Pester 5+) test files and FAILS (throws) unless every test ran and passed.
# A plain `& file.Tests.ps1` with Describe blocks reports failures but never fails the caller (R10e item 2).
param(
    [Parameter(Mandatory = $true)]
    [string[]] $Path
)
$ErrorActionPreference = "Stop"

$pester = Get-Module -ListAvailable -Name Pester | Where-Object { $_.Version -ge [Version]"5.0.0" } |
    Sort-Object Version -Descending | Select-Object -First 1
if ($null -eq $pester) {
    Write-Host "Invoke-CheckpointPesterFile: Pester 5+ not found; installing from PSGallery (CurrentUser)"
    Install-Module -Name Pester -MinimumVersion 5.0.0 -Scope CurrentUser -Force -SkipPublisherCheck
    $pester = Get-Module -ListAvailable -Name Pester | Where-Object { $_.Version -ge [Version]"5.0.0" } |
        Sort-Object Version -Descending | Select-Object -First 1
    if ($null -eq $pester) { throw "Invoke-CheckpointPesterFile: Pester 5+ unavailable after install" }
}
Import-Module $pester.Path -Force
Write-Host "Invoke-CheckpointPesterFile: Pester $($pester.Version) path=$($Path -join ', ')"

foreach ($p in $Path) {
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "Invoke-CheckpointPesterFile: test file missing: $p" }
}

$cfg = New-PesterConfiguration
$cfg.Run.Path = $Path
$cfg.Run.PassThru = $true
$cfg.Run.Exit = $false
$cfg.Output.Verbosity = "Detailed"
$r = Invoke-Pester -Configuration $cfg

if ($null -eq $r) { throw "Invoke-CheckpointPesterFile: Invoke-Pester returned no result" }
$summary = "result=$($r.Result) total=$($r.TotalCount) passed=$($r.PassedCount) failed=$($r.FailedCount) " +
    "skipped=$($r.SkippedCount) notRun=$($r.NotRunCount) failedBlocks=$($r.FailedBlocksCount) failedContainers=$($r.FailedContainersCount)"
Write-Host "Invoke-CheckpointPesterFile: $summary"
if ($r.Result -ne "Passed" -or $r.FailedCount -gt 0 -or $r.FailedBlocksCount -gt 0 -or $r.FailedContainersCount -gt 0 `
        -or $r.TotalCount -eq 0 -or $r.PassedCount -ne $r.TotalCount) {
    throw "Invoke-CheckpointPesterFile: FAILED ($summary) path=$($Path -join ', ')"
}
