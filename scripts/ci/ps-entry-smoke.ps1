#Requires -Version 7.0
param(
    [string]$BaselineCommit = '',
    [switch]$SkipBaselineFailCheck
)

$ErrorActionPreference = 'Stop'

$repoRoot = git rev-parse --show-toplevel 2>$null
if (-not $repoRoot) {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
}

function Get-CiPwshExe {
    if (-not [string]::IsNullOrWhiteSpace($env:CI_PWSH_PATH) -and (Test-Path -LiteralPath $env:CI_PWSH_PATH)) {
        return $env:CI_PWSH_PATH
    }
    foreach ($candidate in @('/usr/bin/pwsh', '/usr/local/bin/pwsh', 'pwsh')) {
        if ($candidate -eq 'pwsh') {
            $cmd = Get-Command -Name pwsh -ErrorAction SilentlyContinue
            if ($cmd -and $cmd.Source -notlike '*\.dotnet\tools\pwsh') {
                return $cmd.Source
            }
            continue
        }
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }
    throw 'pwsh 7 not found (install apt powershell or set CI_PWSH_PATH)'
}

$script:CiPwshExe = Get-CiPwshExe

function Invoke-CiEntrySmoke {
    param(
        [string]$IntegrationScriptPath,
        [string[]]$ExtraArgs
    )

    $smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("gameclub-ci-entry-smoke-" + [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null
    $artifactDir = Join-Path $smokeRoot 'artifacts'
    $logDir = Join-Path $artifactDir 'kiosk-windows-logs'
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $cancelFile = Join-Path $logDir 'deadman.cancel'
    Set-Content -Path $cancelFile -Value 'stale-for-smoke' -Encoding UTF8

    $prevWorkspace = $env:GITHUB_WORKSPACE
    $env:GITHUB_WORKSPACE = $smokeRoot
    & $script:CiPwshExe -NoProfile -ExecutionPolicy Bypass -File $IntegrationScriptPath @ExtraArgs -PcInternetFirewallOnly 2>&1 | ForEach-Object {
        Write-Host $_
    }
    $exit = $LASTEXITCODE
    $phaseLog = Join-Path $logDir 'ci-phase.log'
    return , @{
        ExitCode      = $exit
        PhaseLog      = $phaseLog
        LogDir        = $logDir
        CancelFile    = $cancelFile
        SmokeRoot     = $smokeRoot
        PrevWorkspace = $prevWorkspace
    }
}

function Show-PhaseLog {
    param([string]$PhaseLogPath)
    if (Test-Path -LiteralPath $PhaseLogPath) {
        Get-Content -LiteralPath $PhaseLogPath
    }
    else {
        Write-Host "CI_ENTRY_SMOKE no phase log at $PhaseLogPath"
    }
}

function Cleanup-Smoke {
    param(
        [hashtable]$Result
    )
    $env:GITHUB_WORKSPACE = $Result.PrevWorkspace
    if ($Result.SmokeRoot -and (Test-Path $Result.SmokeRoot)) {
        Remove-Item -Recurse -Force -LiteralPath $Result.SmokeRoot
    }
}

$currentScript = Join-Path $repoRoot 'scripts/windows/ci-integration.ps1'
Write-Host "CI_ENTRY_SMOKE current_tree repo=$repoRoot"
$prevWs = $env:GITHUB_WORKSPACE
$current = Invoke-CiEntrySmoke -IntegrationScriptPath $currentScript -ExtraArgs @('-EntrySmoke')
try {
    if ($current.ExitCode -ne 0) {
        throw "current entry smoke exited $($current.ExitCode)"
    }

    $needle = 'CI_DEADMAN removed stale deadman.cancel reason=script-entry'
    if (-not (Test-Path -LiteralPath $current.PhaseLog)) {
        throw "missing ci-phase.log at $($current.PhaseLog)"
    }
    $phaseText = Get-Content -LiteralPath $current.PhaseLog -Raw
    if ($phaseText -notlike "*$needle*") {
        throw "expected phase log line missing: $needle"
    }
    if (Test-Path -LiteralPath $current.CancelFile) {
        throw 'stale deadman.cancel should have been removed'
    }

    Write-Host 'CI_ENTRY_SMOKE_PASS current'
    Show-PhaseLog -PhaseLogPath $current.PhaseLog
}
finally {
    Cleanup-Smoke -Result $current
}

if (-not $SkipBaselineFailCheck) {
    $baselineSha = $BaselineCommit
    if ([string]::IsNullOrWhiteSpace($baselineSha)) {
        $baselineSha = '4ebbdc60b774d7f5de6aba82f4b56fc90b2220bf'
    }

    $baselineScript = Join-Path ([System.IO.Path]::GetTempPath()) ("ci-integration-$baselineSha.ps1")
    Push-Location $repoRoot
    try {
        $gitShowOutput = @(git show "${baselineSha}:scripts/windows/ci-integration.ps1" 2>&1)
        $gitShowExit = $LASTEXITCODE
        if ($gitShowExit -ne 0) {
            $joined = ($gitShowOutput | ForEach-Object { "$_" }) -join '; '
            throw "CI_ENTRY_SMOKE baseline git show failed sha=$baselineSha exit=$gitShowExit detail=$joined"
        }
        $scriptBody = ($gitShowOutput | ForEach-Object { "$_" }) -join "`n"
        if ([string]::IsNullOrWhiteSpace($scriptBody)) {
            throw "CI_ENTRY_SMOKE baseline git show produced empty script sha=$baselineSha (shallow clone? fetch-depth=1?)"
        }
        Set-Content -LiteralPath $baselineScript -Value $scriptBody -Encoding UTF8
        if (-not (Test-Path -LiteralPath $baselineScript) -or (Get-Item -LiteralPath $baselineScript).Length -lt 64) {
            throw "CI_ENTRY_SMOKE baseline script file missing or too small path=$baselineScript"
        }
    }
    finally {
        Pop-Location
    }

    Write-Host "CI_ENTRY_SMOKE baseline_fail_expected sha=$baselineSha"
    $baseline = Invoke-CiEntrySmoke -IntegrationScriptPath $baselineScript -ExtraArgs @()
    try {
        if ($baseline.ExitCode -eq 0) {
            throw "baseline entry smoke should fail on $baselineSha but exited 0"
        }
        Write-Host "CI_ENTRY_SMOKE_BASELINE_FAIL_OK exit=$($baseline.ExitCode) (expected nonzero)"
        Show-PhaseLog -PhaseLogPath $baseline.PhaseLog
    }
    finally {
        Cleanup-Smoke -Result $baseline
        if (Test-Path -LiteralPath $baselineScript) {
            Remove-Item -Force -LiteralPath $baselineScript
        }
    }
}

Write-Host 'CI_ENTRY_SMOKE_ALL_OK'
exit 0