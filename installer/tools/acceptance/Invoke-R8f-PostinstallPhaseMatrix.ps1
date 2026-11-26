param(
    [string] $ServerSetupHooks,
    [string] $AccountsFile,
    [string] $LowSetup,
    [string] $HighSetup,
    [string] $LowVersion,
    [string] $HighVersion,
    [string] $LogsDir
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")

$script:FreshPhaseOrder = @(
    "webview2", "reinstall-kept-pg", "database", "accounts", "services", "firewall", "backup", "start"
)
$script:UpgradePhaseOrder = @(
    "upgrade-pins", "upgrade-stop", "upgrade-backup", "upgrade-start"
)

function Get-LaterPhases {
    param([string[]] $Order, [string] $Phase)
    $idx = [array]::IndexOf($Order, $Phase)
    if ($idx -lt 0) { return @() }
    if ($idx -ge ($Order.Length - 1)) { return @() }
    return $Order[($idx + 1)..($Order.Length - 1)]
}

function Assert-PostinstallTraceHasNoLaterPhase {
    param(
        [string] $TracePath,
        [string[]] $LaterPhases
    )
    if (-not (Test-Path -LiteralPath $TracePath)) {
        throw "phase matrix: trace missing $TracePath"
    }
    $raw = Get-Content -LiteralPath $TracePath -Raw
    foreach ($later in $LaterPhases) {
        if ($raw -match "(?m)^phase=$([regex]::Escape($later)) status=begin") {
            throw "phase matrix: later phase started after failure: $later"
        }
    }
}

function Test-PhaseRanInTrace {
    param([string] $TracePath, [string] $Phase)
    if (-not (Test-Path -LiteralPath $TracePath)) { return $false }
    $raw = Get-Content -LiteralPath $TracePath -Raw
    return $raw -match "(?m)^phase=$([regex]::Escape($Phase)) status=begin"
}

$script:Trace = "C:\ProgramData\Checkpoint\logs\postinstall-phase-trace.log"

function Test-AcceptanceWebView2Installed {
    # Same key/value as ServerWizard.inc.iss IsWebView2Installed.
    $k = "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
    $pv = (Get-ItemProperty -LiteralPath $k -Name pv -ErrorAction SilentlyContinue).pv
    return -not [string]::IsNullOrWhiteSpace([string]$pv)
}

function Invoke-FreshPhaseFailCase {
    param([string] $Phase)
    $exitCode = Get-CheckpointPostinstallPhaseExitCode -InstallPhase $Phase
    $later = Get-LaterPhases -Order $script:FreshPhaseOrder -Phase $Phase
    # Decide applicability BEFORE running (a phase Setup does not run exits 0 and fails the leg pin).
    if ($Phase -eq "reinstall-kept-pg") {
        Write-Host "R8f-phase-matrix fresh: skip reinstall-kept-pg (needs kept pgdata; not reachable from a reset machine)"
        return
    }
    if ($Phase -eq "webview2" -and (Test-AcceptanceWebView2Installed)) {
        Write-Host "R8f-phase-matrix fresh: skip webview2 (WebView2 runtime already installed on this runner)"
        return
    }
    Write-Host "R8f-phase-matrix fresh: $Phase (exit $exitCode)"
    & (Join-Path $PSScriptRoot "Reset-InstallerAcceptanceMachine.ps1")
    Remove-Item -LiteralPath $script:Trace -Force -ErrorAction SilentlyContinue
    $logPath = Join-Path $LogsDir "acceptance-setup-r8f-phase-fresh-$Phase.log"
    $args = @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
        "/ACCOUNTS=$AccountsFile", "/TESTFAILPHASE=$Phase",
        "/LOG=`"$logPath`""
    )
    $result = Start-CheckpointServerSetup -LegLabel "r8f-phase-fresh-$Phase" -Exe $ServerSetupHooks -ArgumentList $args `
        -LogPath $logPath -ExpectExitCode $exitCode -IntendedPostinstallFailPhase $Phase
    $trace = $script:Trace
    if (-not (Test-PhaseRanInTrace -TracePath $trace -Phase $Phase)) {
        throw "R8f-phase-matrix fresh ${Phase}: phase never began (trace has no begin line)"
    }
    if ($result.ExitCode -ne $exitCode) {
        throw "R8f-phase-matrix fresh ${Phase}: expected exit $exitCode; got $($result.ExitCode)"
    }
    Assert-PostinstallTraceHasNoLaterPhase -TracePath $trace -LaterPhases $later
}

foreach ($phase in $script:FreshPhaseOrder) {
    Invoke-FreshPhaseFailCase -Phase $phase
}

function Invoke-UpgradePhaseFailCase {
    param(
        [string] $Phase,
        [string[]] $ExtraSetupArgs = @()
    )
    $exitCode = Get-CheckpointPostinstallPhaseExitCode -InstallPhase $Phase
    $later = Get-LaterPhases -Order $script:UpgradePhaseOrder -Phase $Phase
    Write-Host "R8f-phase-matrix upgrade: $Phase (exit $exitCode)"
    & (Join-Path $PSScriptRoot "Reset-InstallerAcceptanceMachine.ps1")
    $seedLog = Join-Path $LogsDir "acceptance-setup-r8f-phase-upgrade-seed-$Phase.log"
    Start-CheckpointServerSetup -LegLabel "r8f-phase-upgrade-seed-$Phase" -Exe $LowSetup -ArgumentList @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/ACCOUNTS=$AccountsFile", "/LOG=`"$seedLog`""
    ) -LogPath $seedLog | Out-Null
    Remove-Item -LiteralPath $script:Trace -Force -ErrorAction SilentlyContinue
    $migrationsBefore = Get-InstallerMigrationHistoryFingerprint
    $upStart = Get-Date

    $upLog = Join-Path $LogsDir "acceptance-setup-r8f-phase-upgrade-$Phase.log"
    $env:CHECKPOINT_UPGRADE_FROM = $LowVersion
    $upArgs = @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SKIPSESSIONCHECK=1",
        "/TESTFAILPHASE=$Phase", "/LOG=`"$upLog`""
    ) + $ExtraSetupArgs
    $resultUp = Start-CheckpointServerSetup -LegLabel "r8f-phase-upgrade-$Phase" -Exe $ServerSetupHooks -ArgumentList $upArgs `
        -LogPath $upLog -ExpectExitCode $exitCode -IntendedPostinstallFailPhase $Phase
    $trace = $script:Trace
    if (-not (Test-PhaseRanInTrace -TracePath $trace -Phase $Phase)) {
        throw "R8f-phase-matrix upgrade ${Phase}: phase never began (trace has no begin line)"
    }
    if ($Phase -eq "upgrade-start") {
        # upgrade-backup succeeded in this run, so the restore must start the server (F6: dump exists).
        if (-not (Select-String -LiteralPath $upLog -Pattern "CHECKPOINT_POSTINSTALL_UPGRADE_RESTORE start CheckpointServer dump=" -SimpleMatch)) {
            throw "R8f-phase-matrix upgrade ${Phase}: restore with pre-upgrade dump not logged"
        }
        $svc = Get-Service CheckpointServer -ErrorAction SilentlyContinue
        if (-not $svc -or $svc.Status -ne "Running") {
            throw "R8f-phase-matrix upgrade ${Phase}: CheckpointServer must be Running after restore with dump; status=$($svc.Status)"
        }
    } else {
        Assert-InstallerUpgradeLeftStoppedNoDump -LegLabel "R8f-phase-matrix upgrade $Phase" -InnoLogPath $upLog `
            -MigrationsBefore $migrationsBefore -LegStart $upStart
    }
    if ($resultUp.ExitCode -ne $exitCode) {
        throw "R8f-phase-matrix upgrade ${Phase}: expected $exitCode; got $($resultUp.ExitCode)"
    }
    Assert-PostinstallTraceHasNoLaterPhase -TracePath $trace -LaterPhases $later
}

$defaultPins = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "installer/ci/default-pins-silent-many.json"
foreach ($phase in $script:UpgradePhaseOrder) {
    $extra = @()
    if ($phase -eq "upgrade-pins" -and (Test-Path -LiteralPath $defaultPins)) {
        $extra = @("/DEFAULTPINS=$defaultPins")
    }
    Invoke-UpgradePhaseFailCase -Phase $phase -ExtraSetupArgs $extra
}

Write-Host "R8f-phase-matrix: ok"