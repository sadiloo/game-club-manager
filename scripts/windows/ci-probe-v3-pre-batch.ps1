param([int]$Batch = 1)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot

$utc = [datetime]::UtcNow.ToString("o")
Write-Host "PROBE_V3_PRE_BATCH utc=$utc batch=$Batch"
Write-Host "PROBE_V3_PRE_BATCH hint=logging_only fund_setup_logged_via_STAB_STEP_and_PROBE_HTTP_TRACE"