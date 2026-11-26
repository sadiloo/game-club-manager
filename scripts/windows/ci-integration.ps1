param(
    [switch]$PcInternetFirewallOnly,
    [switch]$EntrySmoke
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot

$artifactDir = Join-Path $env:GITHUB_WORKSPACE "artifacts"
if ([string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE)) {
    $artifactDir = Join-Path $repoRoot "artifacts"
}
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

$logDir = Join-Path $artifactDir "kiosk-windows-logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$env:CI_KIOSK_LOG_DIR = $logDir
$phaseLog = Join-Path $logDir "ci-phase.log"
$timeoutMarker = Join-Path $artifactDir "ci-timeout-marker.log"
$heartbeatLog = Join-Path $logDir "ci-heartbeat.log"
$firewallLog = Join-Path $logDir "ci-firewall.log"
$cancelFile = Join-Path $logDir "deadman.cancel"

function Write-PhaseLog([string]$message) {
    $line = "{0:u}  {1}" -f (Get-Date), $message
    Add-Content -Path $phaseLog -Value $line -Encoding UTF8
}

function Remove-StaleDeadmanCancelFile([string]$Reason) {
    if (-not (Test-Path -LiteralPath $cancelFile)) { return }
    Remove-Item -Force -LiteralPath $cancelFile
    Write-Host "CI_DEADMAN removed stale deadman.cancel reason=$Reason"
    Write-PhaseLog "CI_DEADMAN removed stale deadman.cancel reason=$Reason"
}

Remove-StaleDeadmanCancelFile -Reason "script-entry"

if ($EntrySmoke) {
    if (-not $PcInternetFirewallOnly) {
        throw "CI_ENTRY_SMOKE requires -PcInternetFirewallOnly"
    }
    Write-PhaseLog "START phase=script-entry"
    Write-Host "CI_ENTRY_SMOKE ok logDir=$logDir"
    exit 0
}

if ($PcInternetFirewallOnly) {
    $script:GlobalDeadline = (Get-Date).AddHours(2)
    $script:CiIntegrationStepLabel = "firewall"
} else {
    $script:GlobalDeadline = (Get-Date).AddMinutes(19)
    $script:CiIntegrationStepLabel = "main"
}
$script:FinalExitCode = 0
$script:PhaseLogStartOffset = 0
$script:WatchdogLogStartOffset = 0
if (Test-Path $phaseLog) {
    $script:PhaseLogStartOffset = (Get-Item -LiteralPath $phaseLog).Length
}
$watchdogAtStart = Join-Path $env:ProgramData "Checkpoint\guard\watchdog.log"
if (Test-Path $watchdogAtStart) {
    $script:WatchdogLogStartOffset = (Get-Item -LiteralPath $watchdogAtStart).Length
}
$script:NightLineVstestPhases = @{}
$nightLineToken = "wpf_remaining_time_matches_funded_session_special_band_night_line"

function Assert-TestPhaseExit([string]$PhaseName, [int]$Exit) {
    if ($Exit -eq 124) {
        throw "$PhaseName watchdog"
    }

    if ($Exit -ne 0) {
        throw "$PhaseName failed with exit code $Exit"
    }
}

function Get-VstestConsoleResultCounts {
    param([string]$ConsoleLogPath)

    if ([string]::IsNullOrWhiteSpace($ConsoleLogPath) -or -not (Test-Path -LiteralPath $ConsoleLogPath)) {
        return @{ Executed = 0; Failed = 0 }
    }

    $passed = @(Select-String -Path $ConsoleLogPath -Pattern '(?m)^\s*Passed\s+' -ErrorAction SilentlyContinue).Count
    $failed = @(Select-String -Path $ConsoleLogPath -Pattern '(?m)^\s*Failed\s+' -ErrorAction SilentlyContinue).Count
    return @{ Executed = $passed + $failed; Failed = $failed }
}

function Test-CiGitHubRunnerReachable([string]$label) {
    $ts = (Get-Date).ToUniversalTime().ToString("o")
    $ok = $false
    $detail = "error=unattempted"
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $task = $client.ConnectAsync("github.com", 443)
        if ($task.Wait(5000)) {
            $ok = $client.Connected
            $detail = "tcp=github.com:443 connected=$ok"
        } else {
            $detail = "tcp=github.com:443 timeout_ms=5000"
        }
        $client.Close()
    } catch {
        $detail = "tcp=github.com:443 error=$($_.Exception.Message)"
    }

    $line = "CI_RUNNER_CONNECTIVITY label=$label utc=$ts reachable=$ok $detail"
    Write-Host $line
    Write-PhaseLog $line
}

function Get-CiRunnerProgramAllowList {
    $paths = New-Object System.Collections.Generic.List[string]

    foreach ($runnerName in @("Runner.Listener", "Runner.Worker")) {
        $procs = @(Get-Process -Name $runnerName -ErrorAction SilentlyContinue)
        foreach ($p in $procs) {
            if (-not [string]::IsNullOrWhiteSpace($p.Path)) {
                [void]$paths.Add($p.Path)
                $binDir = Split-Path -Parent $p.Path
                if (Test-Path -LiteralPath $binDir) {
                    Get-ChildItem -Path $binDir -Filter "*.exe" -File -ErrorAction SilentlyContinue | ForEach-Object {
                        if (-not $paths.Contains($_.FullName)) { [void]$paths.Add($_.FullName) }
                    }
                }
            }
        }
    }

    if ($paths.Count -eq 0 -and $env:RUNNER_ROOT) {
        $listener = Join-Path $env:RUNNER_ROOT "bin\Runner.Listener.exe"
        $worker = Join-Path $env:RUNNER_ROOT "bin\Runner.Worker.exe"
        if ((Test-Path -LiteralPath $listener) -and (Test-Path -LiteralPath $worker)) {
            [void]$paths.Add($listener)
            [void]$paths.Add($worker)
        }
    }

    if ($paths.Count -eq 0) {
        throw "CI_RUNNER_ALLOW failed: Runner.Listener and Runner.Worker not found on running processes or RUNNER_ROOT"
    }

    return @($paths | Select-Object -Unique)
}

function Set-CiRunnerInternetExtraAllow {
    $paths = Get-CiRunnerProgramAllowList
    $joined = ($paths -join ";")
    $env:CHECKPOINT_INTERNET_EXTRA_ALLOW = $joined
  Write-Host "CI_RUNNER_ALLOW programs=$joined"
    Write-PhaseLog "CI_RUNNER_ALLOW programs=$joined"
    foreach ($path in $paths) {
        Write-Host "CI_RUNNER_ALLOW path=$path"
        Write-PhaseLog "CI_RUNNER_ALLOW path=$path"
    }
}

function Write-FirewallPartialTrxFromConsole {
    param(
        [string]$ConsoleLogPath,
        [string]$TrxPath
    )

    if ((Test-Path -LiteralPath $TrxPath) -or -not (Test-Path -LiteralPath $ConsoleLogPath)) {
        return
    }

    $failPattern = '\s+(\S+)\s+\[FAIL\]\s*$'
    $results = New-Object System.Collections.Generic.List[object]
    $lines = Get-Content -LiteralPath $ConsoleLogPath -ErrorAction SilentlyContinue
    foreach ($line in $lines) {
        if ($line -notmatch $failPattern) { continue }
        $name = $Matches[1].Trim()
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        $results.Add([pscustomobject]@{ Name = $name; Outcome = "Failed" })
    }

    $passedPattern = '^\s+Passed\s+(.+?)(?:\s+\[\d+ ms\])?\s*$'
    foreach ($line in $lines) {
        if ($line -notmatch $passedPattern) { continue }
        $name = $Matches[1].Trim()
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ($results.Name -contains $name) { continue }
        $results.Add([pscustomobject]@{ Name = $name; Outcome = "Passed" })
    }

    if ($results.Count -eq 0) {
        Write-Host "FW_PARTIAL_TRX skipped reason=no_console_results path=$ConsoleLogPath"
        Write-PhaseLog "FW_PARTIAL_TRX skipped reason=no_console_results path=$ConsoleLogPath"
        return
    }

    $utc = (Get-Date).ToUniversalTime().ToString("o")
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$sb.AppendLine("<TestRun xmlns=`"http://microsoft.com/schemas/VisualStudio/TeamTest/2010`">")
    [void]$sb.AppendLine("  <Results>")
    foreach ($row in $results) {
        $testName = [System.Security.SecurityElement]::Escape([string]$row.Name)
        $outcome = [System.Security.SecurityElement]::Escape([string]$row.Outcome)
        [void]$sb.AppendLine("    <UnitTestResult testName=`"$testName`" outcome=`"$outcome`" />")
    }
    [void]$sb.AppendLine("  </Results>")
    [void]$sb.AppendLine("  <ResultSummary>")
    [void]$sb.AppendLine("    <Counters total=`"$($results.Count)`" executed=`"$($results.Count)`" passed=`"$($results | Where-Object Outcome -eq 'Passed' | Measure-Object | Select-Object -ExpandProperty Count)`" failed=`"$($results | Where-Object Outcome -eq 'Failed' | Measure-Object | Select-Object -ExpandProperty Count)`" />")
    [void]$sb.AppendLine("  </ResultSummary>")
    [void]$sb.AppendLine("</TestRun>")
    Set-Content -LiteralPath $TrxPath -Value $sb.ToString() -Encoding UTF8
    Write-Host "FW_PARTIAL_TRX written path=$TrxPath rows=$($results.Count) utc=$utc source=$ConsoleLogPath"
    Write-PhaseLog "FW_PARTIAL_TRX written path=$TrxPath rows=$($results.Count) utc=$utc source=$ConsoleLogPath"
}

function Write-FirewallPhaseExecutedSummary {
    param(
        [string]$TrxPath,
        [int]$DiscoveredCount,
        [string]$ConsoleLogPath = ""
    )

    $count = 0
    $trxState = "missing"
    if ($TrxPath -and (Test-Path -LiteralPath $TrxPath)) {
        $trxState = "present"
        $results = @(Select-Xml -Path $TrxPath -XPath "//*[local-name()='UnitTestResult']" -ErrorAction SilentlyContinue)
        $count = @($results | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Node.testName) }).Count
    }

    $consoleCounts = Get-VstestConsoleResultCounts -ConsoleLogPath $ConsoleLogPath
    $line = "FW_EXECUTED count=$count discovered=$DiscoveredCount trx=$trxState console_executed=$($consoleCounts.Executed) console_failed=$($consoleCounts.Failed)"
    Write-Host $line
    Write-PhaseLog $line
}

function Assert-FirewallPhaseTestsExecuted {
    param(
        [string]$TrxPath,
        [int]$DiscoveredCount,
        [int]$MinimumCount = 40
    )

    if (-not (Test-Path $TrxPath)) {
        throw "FW_PHASE_TESTS missing_trx path=$TrxPath"
    }

    if ($DiscoveredCount -lt $MinimumCount) {
        throw "FW_PHASE_DISCOVERED count=$DiscoveredCount below_minimum=$MinimumCount"
    }

    $results = @(Select-Xml -Path $TrxPath -XPath "//*[local-name()='UnitTestResult']" -ErrorAction SilentlyContinue)
    $names = @()
    foreach ($row in $results) {
        $name = $row.Node.testName
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        $names += $name
        Write-Host "FW_PHASE_TESTS $name"
        Write-PhaseLog "FW_PHASE_TESTS $name"
    }

    $count = $names.Count
    Write-Host "FW_PHASE_TESTS count=$count discovered=$DiscoveredCount minimum=$MinimumCount"
    Write-PhaseLog "FW_PHASE_TESTS count=$count discovered=$DiscoveredCount minimum=$MinimumCount"
    if ($count -lt $DiscoveredCount) {
        throw "FW_PHASE_TESTS executed=$count discovered=$DiscoveredCount reason=phase_aborted_before_full_inventory"
    }
}

function Write-TimeoutLine([string]$message) {
    $line = "{0:u}  {1}" -f (Get-Date), $message
    Add-Content -Path $phaseLog -Value $line -Encoding UTF8
    Add-Content -Path $timeoutMarker -Value $line -Encoding UTF8
    Write-Host $message
}

function Get-EffectiveTimeoutMs([int]$phaseCapMs) {
    if ($PcInternetFirewallOnly) {
        Write-Host "EFFECTIVE_PHASE_CAP_MS requested=$phaseCapMs effective=$phaseCapMs reason=firewall_only_no_global_clamp"
        Write-PhaseLog "EFFECTIVE_PHASE_CAP_MS requested=$phaseCapMs effective=$phaseCapMs reason=firewall_only_no_global_clamp"
        return [int]$phaseCapMs
    }

    $remaining = ($script:GlobalDeadline - (Get-Date)).TotalMilliseconds - 30000
    if ($remaining -lt 0) { $remaining = 0 }
    $cap = [long]$phaseCapMs
    $effective = if ($remaining -lt $cap) { [int][Math]::Max(0, [int]$remaining) } else { [int]$cap }
    Write-Host "EFFECTIVE_PHASE_CAP_MS requested=$phaseCapMs effective=$effective"
    Write-PhaseLog "EFFECTIVE_PHASE_CAP_MS requested=$phaseCapMs effective=$effective"
    return $effective
}

function Get-NewLogSubstring([string]$path, [long]$startOffset) {
    if (-not (Test-Path $path)) {
        return ""
    }

    $len = (Get-Item -LiteralPath $path).Length
    if ($len -le $startOffset) {
        return ""
    }

    $fs = [System.IO.File]::OpenRead($path)
    try {
        $null = $fs.Seek($startOffset, [System.IO.SeekOrigin]::Begin)
        $toRead = [int]($len - $startOffset)
        $buf = New-Object byte[] $toRead
        $read = $fs.Read($buf, 0, $toRead)
        return [System.Text.Encoding]::UTF8.GetString($buf, 0, $read)
    } finally {
        $fs.Dispose()
    }
}

function Get-ProcessTreeLines([int]$rootProcId) {
    $lines = New-Object System.Collections.Generic.List[string]
    $queue = [System.Collections.Generic.Queue[int]]::new()
    $queue.Enqueue($rootProcId)
    $seen = @{}
    while ($queue.Count -gt 0) {
        $procId = $queue.Dequeue()
        if ($seen.ContainsKey($procId)) { continue }
        $seen[$procId] = $true
        $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$procId" -ErrorAction SilentlyContinue
        if (-not $proc) { continue }
        $cmd = if ($proc.CommandLine) { $proc.CommandLine } else { "" }
        $lines.Add("pid=$($proc.ProcessId) name=$($proc.Name) cmdline=$cmd")
        $children = Get-CimInstance Win32_Process -Filter "ParentProcessId=$procId" -ErrorAction SilentlyContinue
        foreach ($child in $children) {
            $queue.Enqueue([int]$child.ProcessId)
        }
    }
    return $lines
}

function Write-ProcessTree([int]$rootProcId) {
    foreach ($line in (Get-ProcessTreeLines -rootProcId $rootProcId)) {
        Write-PhaseLog "PROCESS_TREE $line"
        Write-Host "PROCESS_TREE $line"
    }
}

function Stop-ProcessTree([int]$rootProcId) {
    & taskkill.exe /T /F /PID $rootProcId 2>$null | Out-Null
    # taskkill on an already-dead PID leaves a non-zero exit code; do not leak it to the GitHub pwsh wrapper.
    $global:LASTEXITCODE = 0
}

function Stop-OrphanedGuardAndKiosk([string]$Reason) {
    foreach ($name in @("GameClub.Kiosk.Guard", "GameClub.Kiosk")) {
        foreach ($p in @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq $name })) {
            Write-Host "CI_ORPHAN_KILL reason=$Reason name=$name pid=$($p.Id)"
            Stop-ProcessTree -rootProcId $p.Id
        }
    }
}

function Write-GameClubServerProcessAudit() {
    foreach ($p in Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -match 'GameClub\.Server\.dll' }) {
        $cmd = if ($p.CommandLine) { $p.CommandLine } else { "" }
        Write-PhaseLog "SERVER_PROCESS_SEEN pid=$($p.ProcessId) cmdline=$cmd"
    }
}

function Stop-CiManagedGameClubServerPids() {
    $stopPids = @()
    if ($null -ne $script:serverProc -and -not $script:serverProc.HasExited) {
        $stopPids += $script:serverProc.Id
    }

    if ($env:GAMECLUB_SERVER_PID -match '^\d+$') {
        $stopPids += [int]$env:GAMECLUB_SERVER_PID
    }

    $pidFile = Join-Path $logDir "live-server.pid"
    if (Test-Path $pidFile) {
        foreach ($line in (Get-Content $pidFile -ErrorAction SilentlyContinue)) {
            $trim = if ($null -ne $line) { $line.Trim() } else { "" }
            if ($trim -match '^\d+$') {
                $stopPids += [int]$trim
            }
        }
    }

    foreach ($managedPid in ($stopPids | Select-Object -Unique)) {
        Write-PhaseLog "STOP_MANAGED_SERVER pid=$managedPid"
        try {
            Stop-ProcessTree -rootProcId $managedPid
        } catch { }
    }

    $global:LASTEXITCODE = 0
    $script:serverProc = $null
    Write-GameClubServerProcessAudit
}

function Test-TcpPortTaken([int]$Port) {
    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $client.Connect('127.0.0.1', $Port)
        $client.Close()
        return $true
    } catch {
        return $false
    }
}

function Wait-CiServerPortFree([int]$Port, [int]$TimeoutSec) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-TcpPortTaken -Port $Port)) { return }
        Start-Sleep -Seconds 1
    }
    throw "TCP port 127.0.0.1:${Port} still in use after ${TimeoutSec}s; orphaned GameClub.Server must be stopped before continuing."
}

function Stop-OrphanedGameClubTestHosts() {
    foreach ($p in Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -match 'GameClub\.Server\.TestHost' }) {
        Write-PhaseLog "STOP_ORPHAN_TESTHOST pid=$($p.ProcessId)"
        Stop-ProcessTree -rootProcId $p.ProcessId
    }
    $global:LASTEXITCODE = 0
}

function Assert-TestHostPcBandLog([string]$LogPath, [string]$ExpectedBand) {
    if (-not (Test-Path $LogPath)) {
        throw "TestHost stdout log missing at $LogPath"
    }
    $production = @(Select-String -Path $LogPath -Pattern '^pc_band=production\b' -ErrorAction SilentlyContinue)
    if ($production.Count -gt 0) {
        throw "GameClub.Server.TestHost logged pc_band=production; --pc-band is required."
    }
    $matches = @(Select-String -Path $LogPath -Pattern "^pc_band=$ExpectedBand\b" -ErrorAction SilentlyContinue)
    if ($matches.Count -eq 0) {
        throw "TestHost log has no pc_band=$ExpectedBand line (path=$LogPath)."
    }
    foreach ($bandLine in $matches) {
        Write-PhaseLog $bandLine.Line
        Write-Host $bandLine.Line
    }
}

function Start-CiGameClubTestHost(
    [string]$ServerDll,
    [string[]]$BandArgs,
    [string]$StdoutLog,
    [string]$StderrLog,
    [string]$ExpectedBand,
    [string]$PhaseLabel
) {
    Stop-CiManagedGameClubServerPids
    Stop-OrphanedGameClubTestHosts
    $uri = [Uri]$env:CHECKPOINT_SERVER_URL
    $port = if ($uri.Port -gt 0) { $uri.Port } else { 80 }
    Wait-CiServerPortFree -Port $port -TimeoutSec 45
    if (Test-TcpPortTaken -Port $port) {
        Write-GameClubServerProcessAudit
        throw "Port ${port} still taken before $PhaseLabel server start."
    }

    if (Test-Path $StdoutLog) { Remove-Item -Force $StdoutLog }
    if (Test-Path $StderrLog) { Remove-Item -Force $StderrLog }

    $env:GAMECLUB_SERVER_HOST_ARGS = ($BandArgs -join ' ')
    Write-Host "live_server_ci $PhaseLabel band_args=$($env:GAMECLUB_SERVER_HOST_ARGS)"
    Write-PhaseLog "START $PhaseLabel host_args=$($env:GAMECLUB_SERVER_HOST_ARGS)"
    $script:serverProc = Start-Process -FilePath "dotnet" -ArgumentList (@("exec", $ServerDll) + $BandArgs) -PassThru -NoNewWindow `
        -RedirectStandardOutput $StdoutLog -RedirectStandardError $StderrLog -WorkingDirectory $repoRoot
    $null = $script:serverProc.Handle
    $env:GAMECLUB_SERVER_PID = $script:serverProc.Id
    $env:GAMECLUB_SERVER_DLL = $ServerDll

    $readyDeadline = (Get-Date).AddMinutes(2)
    $ready = $false
    while ((Get-Date) -lt $readyDeadline) {
        try {
            $r = Invoke-WebRequest -Uri "$($env:CHECKPOINT_SERVER_URL)/api/stations" -Headers @{ "X-Api-Key" = $env:GAMECLUB_API_KEY } -UseBasicParsing -TimeoutSec 3
            if ($r.StatusCode -eq 200) { $ready = $true; break }
        } catch {
            Start-Sleep -Seconds 2
        }
    }
    if (-not $ready) {
        if ($script:serverProc -and -not $script:serverProc.HasExited) {
            Stop-ProcessTree -rootProcId $script:serverProc.Id
        }
        throw "GameClub.Server.TestHost not ready for $PhaseLabel"
    }

    Start-Sleep -Seconds 1
    Assert-TestHostPcBandLog -LogPath $StdoutLog -ExpectedBand $ExpectedBand
}

function Stop-LeftoverCiProcesses() {
    Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -like "GameClub.Kiosk*" -or
        $_.ProcessName -like "*Guard*" -or
        $_.ProcessName -like "testhost*"
    } | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
}

function Write-CiPhaseFirewallProbe([string]$label) {
    Test-CiGitHubRunnerReachable -label $label
    Write-PhaseLog "PHASE boundary $label"
    Write-CiFirewallStoreEcho -Label $label
    Write-CiFirewallEvidence -Path $firewallLog -Label $label
    Write-CiEgressToLogs -FirewallLog $firewallLog -WritePhase { param($line) Write-PhaseLog $line }
    Test-CiGitHubRunnerReachable -label "$label-post"
}

function Assert-CiPipeAndProcessLeakFromLogs {
    $step = $script:CiIntegrationStepLabel
    $watchdogPath = Join-Path $env:ProgramData "Checkpoint\guard\watchdog.log"
    $token = "pipe_background_join_timeout"
    $joinCount = 0
    $phaseTail = Get-NewLogSubstring -path $phaseLog -startOffset $script:PhaseLogStartOffset
    if (-not [string]::IsNullOrEmpty($phaseTail)) {
        $joinCount += @([regex]::Matches($phaseTail, [regex]::Escape($token))).Count
    }

    $watchdogTail = Get-NewLogSubstring -path $watchdogPath -startOffset $script:WatchdogLogStartOffset
    if (-not [string]::IsNullOrEmpty($watchdogTail)) {
        $joinCount += @([regex]::Matches($watchdogTail, [regex]::Escape($token))).Count
    }

    Write-Host "CI_PIPE_BACKGROUND_JOIN_TIMEOUT count=$joinCount step=$step"
    Write-PhaseLog "CI_PIPE_BACKGROUND_JOIN_TIMEOUT count=$joinCount step=$step"
    if ($joinCount -gt 0 -and $script:FinalExitCode -eq 0) {
        $script:FinalExitCode = 1
    }

    $leakKiosk = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq "GameClub.Kiosk" })
    Write-Host "CI_PROCESS_LEAK_KIOSK count=$($leakKiosk.Count) step=$step"
    if ($leakKiosk.Count -gt 0 -and $script:FinalExitCode -eq 0) {
        $script:FinalExitCode = 1
    }
}

function Ensure-PcInternetFirewallBuildOutputs {
    $missing = New-Object System.Collections.Generic.List[string]
    $pairs = @(
        @{
            Path = Join-Path $repoRoot "tests/GameClub.Kiosk.Core.Tests/bin/Release/net10.0/GameClub.Kiosk.Core.Tests.dll"
            Args = @(
                "build", "tests/GameClub.Kiosk.Core.Tests/GameClub.Kiosk.Core.Tests.csproj",
                "-c", "Release", "-f", "net10.0", "--nologo")
        },
        @{
            Path = Join-Path $repoRoot "tests/GameClub.Kiosk.Windows.IntegrationTests/bin/Release/net48/GameClub.Kiosk.Windows.IntegrationTests.dll"
            Args = @(
                "build", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj",
                "-c", "Release", "-f", "net48", "--nologo")
        },
        @{
            Path = Join-Path $repoRoot "tests/GameClub.Kiosk.Windows.IntegrationTests/bin/Release/net10.0/GameClub.Kiosk.Windows.IntegrationTests.dll"
            Args = @(
                "build", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj",
                "-c", "Release", "-f", "net10.0", "--nologo")
        },
        @{
            Path = Join-Path $repoRoot "src/GameClub.Kiosk.Guard/bin/Release/net48/GameClub.Kiosk.Guard.exe"
            Args = @("build", "src/GameClub.Kiosk.Guard/GameClub.Kiosk.Guard.csproj", "-c", "Release", "-f", "net48", "--nologo")
        },
        @{
            Path = Join-Path $repoRoot "tests/GameClub.Server.TestHost/bin/Release/net10.0/GameClub.Server.TestHost.dll"
            Args = @(
                "build", "tests/GameClub.Server.TestHost/GameClub.Server.TestHost.csproj",
                "-c", "Release", "--nologo")
        }
    )
    foreach ($pair in $pairs) {
        if (Test-Path $pair.Path) { continue }
        $rel = $pair.Path.Substring($repoRoot.Length).TrimStart('\', '/')
        $missing.Add($rel)
        Write-Host "FW_ONLY_BUILD reason=missing_output path=$rel"
        Write-PhaseLog "FW_ONLY_BUILD reason=missing_output path=$rel"
        & dotnet @($pair.Args)
        if ($LASTEXITCODE -ne 0) {
            throw "FW_ONLY_BUILD failed path=$rel"
        }
    }

    if ($missing.Count -eq 0) {
        Write-Host "FW_ONLY_BUILD reason=skip_existing_outputs"
        Write-PhaseLog "FW_ONLY_BUILD reason=skip_existing_outputs"
    }
}

function Write-VstestPhaseList([string]$phaseName, [string]$framework, [string]$filterExpr) {
    $listArgs = @(
        "test", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj",
        "-c", "Release", "-f", $framework, "--no-build", "--nologo",
        "--list-tests", "--filter", $filterExpr
    )
    $listOut = & dotnet @listArgs 2>&1
    $names = @($listOut | Where-Object { $_ -match '^\s+GameClub\.Kiosk\.Windows\.IntegrationTests\.' })
    Write-PhaseLog "VSTEST_LIST phase=$phaseName framework=$framework count=$($names.Count) filter=$filterExpr"
  $hasNightLine = $false
    foreach ($name in $names) {
        $trim = $name.Trim()
        Write-PhaseLog "VSTEST_LIST_TEST phase=$phaseName $trim"
        Write-Host "VSTEST_LIST_TEST phase=$phaseName $trim"
    if ($trim -like "*${nightLineToken}*") {
            $hasNightLine = $true
        }
    }
  if ($hasNightLine) {
        $script:NightLineVstestPhases[$phaseName] = $true
    }
    return $names
}

function Publish-ServerLeaseBuildEvidence([string]$phaseName, [string]$TrxPath) {
    $allLogs = @(
        (Join-Path $logDir "server-day-out.log"),
        (Join-Path $logDir "server-night-out.log"),
        (Join-Path $logDir "server-day-err.log"),
        (Join-Path $logDir "server-night-err.log")
    )
    $allLogs += @(Get-ChildItem -Path $logDir -Filter 'server-relaunch-*-out.log' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    $reconnectLogs = @()

    foreach ($logPath in $allLogs) {
        if (-not (Test-Path $logPath)) { continue }
        $destName = [IO.Path]::GetFileName($logPath)
        $dest = Join-Path $artifactDir $destName
        Copy-Item -Path $logPath -Destination $dest -Force
        $lines = @(Select-String -Path $logPath -Pattern '^LEASE_BUILD\b' -ErrorAction SilentlyContinue)
        foreach ($line in $lines) {
            Write-PhaseLog "LEASE_BUILD phase=$phaseName sourceLog=$destName $($line.Line)"
            Write-Host "LEASE_BUILD phase=$phaseName sourceLog=$destName $($line.Line)"
        }
        if ($logPath -like '*server-relaunch-*-out.log' -or $destName -eq 'server-day-out.log') {
            $reconnectLogs += $logPath
        }
    }

    if ($phaseName -ne 'dotnet-test-net10-server') { return $null }
    if (-not (Test-Path $TrxPath)) { return $null }
    [xml]$trx = Get-Content $TrxPath
    $ranReconnect = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.testName -like '*live_server_kiosk_reconnects_when_server_unreachable_at_start*' })
    if ($ranReconnect.Count -eq 0) { return $null }
    $found = $false
    foreach ($logPath in ($reconnectLogs | Select-Object -Unique)) {
        if (-not (Test-Path $logPath)) { continue }
        $leaseLines = @(Select-String -Path $logPath -Pattern '^LEASE_BUILD\b' -ErrorAction SilentlyContinue)
        if ($leaseLines.Count -gt 0) {
            $found = $true
        }
    }
    if (-not $found) {
        return 'LEASE_BUILD missing'
    }
    return $null
}

function Assert-NightLineOnlyInNightPhase {
    $allowed = @('dotnet-test-net48-wpf-night')
    foreach ($phase in $script:NightLineVstestPhases.Keys) {
        if ($allowed -notcontains $phase) {
            throw "night_line test must not appear in phase $phase"
        }
    }
    if (-not $script:NightLineVstestPhases.ContainsKey('dotnet-test-net48-wpf-night')) {
        throw "night_line test missing from dotnet-test-net48-wpf-night phase list"
    }
}

function Invoke-DotNetWithWatchdog {
    param(
        [string]$PhaseName,
        [string[]]$ArgumentList,
        [int]$PhaseCapMs,
        [string]$DiagLogPath = "",
        [string]$StdOutLog = "",
        [string]$StdErrLog = ""
    )
    $timeoutMs = Get-EffectiveTimeoutMs -phaseCapMs $PhaseCapMs
    if ($timeoutMs -le 0) {
        $sec = 0
        $msg = "WATCHDOG TIMEOUT $PhaseName after ${sec}s (no time left on global deadline)"
        Write-TimeoutLine $msg
        Write-PhaseLog "WATCHDOG: dotnet-dump not available; skipped"
        $script:FinalExitCode = 124
        return 124
    }

    Write-PhaseLog "START $PhaseName"
    $dotnetArgs = $ArgumentList
    if (-not [string]::IsNullOrWhiteSpace($DiagLogPath)) {
        $dotnetArgs = $ArgumentList + @("--diag:$DiagLogPath")
    }
    $startParams = @{
        FilePath     = "dotnet"
        ArgumentList = $dotnetArgs
        PassThru     = $true
        NoNewWindow  = $true
        WorkingDirectory = $repoRoot
    }
    if (-not [string]::IsNullOrWhiteSpace($StdOutLog)) {
        $startParams["RedirectStandardOutput"] = $StdOutLog
    }
    if (-not [string]::IsNullOrWhiteSpace($StdErrLog)) {
        $startParams["RedirectStandardError"] = $StdErrLog
    }
    $proc = Start-Process @startParams
    $null = $proc.Handle
    $exited = $proc.WaitForExit($timeoutMs)
    if (-not $exited) {
        $elapsedSec = [int]($timeoutMs / 1000)
        $msg = "WATCHDOG TIMEOUT $PhaseName after ${elapsedSec}s"
        Write-TimeoutLine $msg
        Write-PhaseLog "WATCHDOG: dotnet-dump not available; skipped"
        Write-ProcessTree -rootProcId $proc.Id
        Stop-ProcessTree -rootProcId $proc.Id
        Stop-LeftoverCiProcesses
        Write-PhaseLog "END $PhaseName exit=124"
        $script:FinalExitCode = 124
        return 124
    }
    Write-PhaseLog "END $PhaseName exit=$($proc.ExitCode)"
    if ($proc.ExitCode -ne 0) {
        if ($script:FinalExitCode -eq 0) { $script:FinalExitCode = $proc.ExitCode }
    }
    return $proc.ExitCode
}

if (-not $env:GAMECLUB_TEST_DATABASE) {
    $env:GAMECLUB_TEST_DATABASE = "Host=localhost;Port=5432;Database=gameclub_test_ci;Username=postgres;Password=postgres;SSL Mode=Disable"
    $env:GAMECLUB_DATABASE = $env:GAMECLUB_TEST_DATABASE
    $env:GAMECLUB_API_KEY = "ci-test-key"
}

$script:ServerPcBandArgs = @('--pc-band', 'day')

function Set-CiServerPcBandArgs([ValidateSet('day','night')][string]$Mode) {
    $script:ServerPcBandArgs = @('--pc-band', $Mode)
}

Set-CiServerPcBandArgs -Mode day

if (-not $env:CHECKPOINT_SERVER_URL) {
    $env:CHECKPOINT_SERVER_URL = "http://127.0.0.1:5225"
}
if (-not $env:OperatorSessionSecret) {
    $env:OperatorSessionSecret = "ci-operator-secret-r11"
}
if (-not $env:ASPNETCORE_URLS) {
    $env:ASPNETCORE_URLS = $env:CHECKPOINT_SERVER_URL
}
$env:ASPNETCORE_ENVIRONMENT = "Production"

$kioskAccountName = "CheckpointKiosk"
$guestUser = "CheckpointGuest"
$guestPassword = "CkPt!" + ([Guid]::NewGuid().ToString("N").Substring(0, 16))
$kioskPassword = "CkPt!" + ([Guid]::NewGuid().ToString("N").Substring(0, 16))

function Ensure-LocalUser([string]$name, [string]$password) {
    $existing = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
    if ($existing) {
        Set-LocalUser -Name $name -Password (ConvertTo-SecureString $password -AsPlainText -Force)
        return
    }
    New-LocalUser -Name $name -Password (ConvertTo-SecureString $password -AsPlainText -Force) -UserMayNotChangePassword -PasswordNeverExpires | Out-Null
    Add-LocalGroupMember -Group "Users" -Member $name -ErrorAction SilentlyContinue
}

function Ensure-KioskRegistryHiveLoaded([string]$accountName, [string]$sid) {
    $profileDir = Join-Path $env:SystemDrive "Users\$accountName"
    $ntuser = Join-Path $profileDir "NTUSER.DAT"
    if (-not (Test-Path $profileDir)) {
        New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
    }
    if (-not (Test-Path $ntuser)) {
        $defaultHive = Join-Path $env:SystemDrive "Users\Default\NTUSER.DAT"
        if (-not (Test-Path $defaultHive)) {
            $defaultHive = Join-Path $env:windir "System32\config\Default"
        }
        if (-not (Test-Path $defaultHive)) {
            throw "Default user hive missing; cannot load $accountName policy hive."
        }
        Copy-Item -Path $defaultHive -Destination $ntuser -Force
    }
    $hiveRoot = "HKU\$sid"
    reg query $hiveRoot 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        reg load $hiveRoot $ntuser | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "reg load failed for $accountName ($sid)" }
    }
}

$script:serverProc = $null
$serverTestExit = 0
$guardTestExit = 0

. (Join-Path $PSScriptRoot "ci-firewall-restore.ps1")
Save-CiFirewallInitialState -LogDir $logDir | Out-Null
Write-PhaseLog "START phase=script-entry"

$scriptStartUtc = (Get-Date).ToUniversalTime()
$deadmanDeadlineUtc = $scriptStartUtc.AddMinutes(18).ToString("o")
$deadmanLog = Join-Path $logDir "deadman.supervisor.log"
Remove-StaleDeadmanCancelFile -Reason "before-deadman-arm"
$deadmanScript = Join-Path $PSScriptRoot "ci-integration-deadman.ps1"
$deadmanArgs = @(
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $deadmanScript,
    "-DeadlineUtcIso", $deadmanDeadlineUtc,
    "-CancelFilePath", $cancelFile,
    "-MarkerPath", $timeoutMarker,
    "-SupervisorLogPath", $deadmanLog,
    "-PhaseLogPath", $phaseLog,
    "-HeartbeatLogPath", $heartbeatLog,
    "-LogDir", $logDir,
    "-RepoRoot", $repoRoot
)
$supervisorProc = Start-Process -FilePath "pwsh" -ArgumentList $deadmanArgs -PassThru -NoNewWindow -WorkingDirectory $repoRoot
$null = $supervisorProc.Handle
$deadmanLabel = if ($PcInternetFirewallOnly) { "firewall-step" } else { "main-integration" }
Write-PhaseLog "CI_DEADMAN armed step=$deadmanLabel pid=$($supervisorProc.Id) deadline=$deadmanDeadlineUtc cancelFile=$cancelFile"
Write-Host "CI_DEADMAN armed step=$deadmanLabel pid=$($supervisorProc.Id) deadline=$deadmanDeadlineUtc"

Write-CiPhaseFirewallProbe -label "pre-integration"

function Assert-GameClubServerCiEnv {
    $errors = New-Object System.Collections.Generic.List[string]
    $dll = $env:GAMECLUB_SERVER_DLL
    if ([string]::IsNullOrWhiteSpace($dll)) {
        $errors.Add("GAMECLUB_SERVER_DLL is not set")
    }
    elseif (-not (Test-Path -LiteralPath $dll)) {
        $errors.Add("GAMECLUB_SERVER_DLL not found at $dll")
    }

    if ($env:GAMECLUB_SERVER_PID -notmatch '^\d+$') {
        $errors.Add("GAMECLUB_SERVER_PID is not set")
    }
    else {
        $serverPid = [int]$env:GAMECLUB_SERVER_PID
        try {
            $proc = Get-Process -Id $serverPid -ErrorAction Stop
            if ($proc.HasExited) {
                $errors.Add("GAMECLUB_SERVER_PID=$serverPid has exited")
            }
        }
        catch {
            $errors.Add("GAMECLUB_SERVER_PID=$serverPid is not running")
        }
    }

    if ($errors.Count -gt 0) {
        throw "GAMECLUB_SERVER_PID and GAMECLUB_SERVER_DLL must be set on windows-2022 integration. $($errors -join '; ')"
    }

    Write-Host "CI_GAMECLUB_SERVER env ok pid=$($env:GAMECLUB_SERVER_PID) dll=$dll"
    Write-PhaseLog "CI_GAMECLUB_SERVER env ok pid=$($env:GAMECLUB_SERVER_PID) dll=$dll"
}

function Invoke-PcInternetFirewallIntegrationPhase {
    Assert-GameClubServerCiEnv
    Write-Host "Running PC internet off verification unit tests (Category=PcInternetFirewall, Core)..."
    $coreFwFilter = "Category=PcInternetFirewall&FullyQualifiedName~PcInternetFirewallOffVerificationTests"
    Write-VstestPhaseList -phaseName "dotnet-test-pc-internet-firewall-core" -framework "net10.0" -filterExpr $coreFwFilter | Out-Null
    $coreFwArgs = @(
        "test", "tests/GameClub.Kiosk.Core.Tests/GameClub.Kiosk.Core.Tests.csproj",
        "-c", "Release", "-f", "net10.0", "--no-build", "--nologo",
        "--filter", $coreFwFilter,
        "--logger", "console;verbosity=detailed"
    )
    $coreFwExit = Invoke-DotNetWithWatchdog -PhaseName "dotnet-test-pc-internet-firewall-core" -ArgumentList $coreFwArgs -PhaseCapMs (2 * 60 * 1000)
    Assert-TestPhaseExit -PhaseName "dotnet-test-pc-internet-firewall-core" -Exit $coreFwExit

    Write-Host "Running PC internet firewall integration (Category=PcInternetFirewall)..."
    Write-CiPhaseFirewallProbe -label "before-dotnet-test-pc-internet-firewall"
    $fwFilter = "Category=PcInternetFirewall"
    $fwDiscoveredNames = @(Write-VstestPhaseList -phaseName "dotnet-test-pc-internet-firewall" -framework "net48" -filterExpr $fwFilter)
    foreach ($fwName in $fwDiscoveredNames) {
        $trim = $fwName.Trim()
        Write-Host "FW_PHASE_DISCOVERED $trim"
        Write-PhaseLog "FW_PHASE_DISCOVERED $trim"
    }
    Write-Host "FW_PHASE_DISCOVERED count=$($fwDiscoveredNames.Count)"
    Write-PhaseLog "FW_PHASE_DISCOVERED count=$($fwDiscoveredNames.Count)"
    if ($fwDiscoveredNames.Count -lt 40) {
        throw "FW_PHASE_DISCOVERED count=$($fwDiscoveredNames.Count) below_minimum=40"
    }
    $fwTestArgs = @(
        "test", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj",
        "-c", "Release", "-f", "net48", "--no-build", "--nologo",
        "--filter", $fwFilter,
        "--logger", "trx;LogFileName=kiosk-windows-pc-internet-firewall.trx",
        "--results-directory", $artifactDir,
        "--logger", "console;verbosity=detailed"
    )
    $fwTrxPath = Join-Path $artifactDir "kiosk-windows-pc-internet-firewall.trx"
    $fwConsoleLog = Join-Path $artifactDir "kiosk-windows-pc-internet-firewall-console.log"
    $fwPhaseCapMs = 18 * 60 * 1000
    $fwEffectiveCapMs = Get-EffectiveTimeoutMs -phaseCapMs $fwPhaseCapMs
    Write-Host "FW_PHASE_BUDGET cap_ms=$fwPhaseCapMs effective_ms=$fwEffectiveCapMs basis_measured_ms=695000 step_timeout_min=22"
    Write-PhaseLog "FW_PHASE_BUDGET cap_ms=$fwPhaseCapMs effective_ms=$fwEffectiveCapMs basis_measured_ms=695000 step_timeout_min=22"
    $fwTestExit = 0
    try {
        $fwTestExit = Invoke-DotNetWithWatchdog -PhaseName "dotnet-test-pc-internet-firewall" -ArgumentList $fwTestArgs -PhaseCapMs $fwEffectiveCapMs -StdOutLog $fwConsoleLog
        Publish-ServerLeaseBuildEvidence -phaseName "dotnet-test-pc-internet-firewall" -TrxPath $fwTrxPath
        Write-CiPhaseFirewallProbe -label "after-dotnet-test-pc-internet-firewall"
        Assert-TestPhaseExit -PhaseName "dotnet-test-pc-internet-firewall" -Exit $fwTestExit
        Assert-FirewallPhaseTestsExecuted -TrxPath $fwTrxPath -DiscoveredCount $fwDiscoveredNames.Count
    }
    finally {
        Write-FirewallPartialTrxFromConsole -ConsoleLogPath $fwConsoleLog -TrxPath $fwTrxPath
        if ($fwTestExit -eq 124) {
            $watchdogRestoreLog = Join-Path $logDir "ci-firewall-watchdog-restore.log"
            try {
                Invoke-CiFirewallRestore -LogDir $logDir -FirewallLog $watchdogRestoreLog
                Write-Host "FW_WATCHDOG_RESTORE completed log=$watchdogRestoreLog"
                Write-PhaseLog "FW_WATCHDOG_RESTORE completed log=$watchdogRestoreLog"
            }
            catch {
                Write-Host "FW_WATCHDOG_RESTORE failed: $_"
                Write-PhaseLog "FW_WATCHDOG_RESTORE failed: $_"
            }
        }

        Write-FirewallPhaseExecutedSummary -TrxPath $fwTrxPath -DiscoveredCount $fwDiscoveredNames.Count -ConsoleLogPath $fwConsoleLog
    }
}

try {
    try {
        Ensure-LocalUser -name $kioskAccountName -password $kioskPassword
        Ensure-LocalUser -name $guestUser -password $guestPassword
        $guestProbe = Get-LocalUser -Name $guestUser -ErrorAction Stop
        if ($guestProbe.Enabled -eq $false) { Enable-LocalUser -Name $guestUser }
    } catch {
        Write-Error "Local user provisioning failed: $_"
        $script:FinalExitCode = 1
        throw
    }

    $kioskSid = (New-Object System.Security.Principal.NTAccount($kioskAccountName)).Translate([System.Security.Principal.SecurityIdentifier]).Value
    Ensure-KioskRegistryHiveLoaded -accountName $kioskAccountName -sid $kioskSid
    $env:CHECKPOINT_KIOSK_SID = $kioskSid
    $loggedOnUser = (Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction SilentlyContinue).UserName
    if (-not [string]::IsNullOrWhiteSpace($loggedOnUser)) {
        $env:CHECKPOINT_KIOSK_USER = $loggedOnUser.Split('\')[-1]
    } else {
        $env:CHECKPOINT_KIOSK_USER = $env:USERNAME
    }
    $env:CHECKPOINT_SECOND_USER = $guestUser
    $env:CHECKPOINT_SECOND_PASSWORD = $guestPassword
    $env:CHECKPOINT_GUARD_CI_RUNNER_LAUNCH = "1"
    Set-CiRunnerInternetExtraAllow

    $env:PGPASSWORD = "postgres"

    if ($PcInternetFirewallOnly) {
        Write-PhaseLog "START mode=pc-internet-firewall-only"
        Set-CiRunnerInternetExtraAllow

        Ensure-PcInternetFirewallBuildOutputs
        $serverDll = Join-Path $repoRoot "tests/GameClub.Server.TestHost/bin/Release/net10.0/GameClub.Server.TestHost.dll"
        if (-not (Test-Path $serverDll)) {
            throw "GameClub.Server.TestHost.dll not found at $serverDll after FW_ONLY_BUILD"
        }
        $serverLog = Join-Path $logDir "server-firewall-only-out.log"
        $serverErr = Join-Path $logDir "server-firewall-only-err.log"
        Start-CiGameClubTestHost -ServerDll $serverDll -BandArgs $script:ServerPcBandArgs -StdoutLog $serverLog -StderrLog $serverErr -ExpectedBand day -PhaseLabel "firewall-only"
        Invoke-PcInternetFirewallIntegrationPhase
    } else {

    $buildCapMs = 8 * 60 * 1000
    $buildDeadline = (Get-Date).AddMilliseconds([double](Get-EffectiveTimeoutMs -phaseCapMs $buildCapMs))
    $globalBuildCap = ($script:GlobalDeadline.AddSeconds(-30))
    if ($buildDeadline -gt $globalBuildCap) { $buildDeadline = $globalBuildCap }

    Write-PhaseLog "START phase=builds"
    Write-Host "Building Release (-warnaserror)..."
    $builds = @(
        @("build", "GameClub.slnx", "-c", "Release", "-warnaserror", "--nologo"),
        @("build", "src/GameClub.Kiosk/GameClub.Kiosk.csproj", "-c", "Release", "-f", "net48", "--nologo"),
        @("build", "src/GameClub.Kiosk.Guard/GameClub.Kiosk.Guard.csproj", "-c", "Release", "-f", "net48", "--nologo"),
        @("build", "src/GameClub.Server/GameClub.Server.csproj", "-c", "Release", "--nologo"),
        @("build", "tests/GameClub.Server.TestHost/GameClub.Server.TestHost.csproj", "-c", "Release", "--nologo"),
        @("build", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj", "-c", "Release", "-f", "net48", "--nologo"),
        @("build", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj", "-c", "Release", "-f", "net10.0", "--nologo")
    )
    $buildIdx = 0
    foreach ($buildArgs in $builds) {
        $buildIdx++
        $remainingMs = [int](($buildDeadline - (Get-Date)).TotalMilliseconds)
        if ($remainingMs -le 0) {
            $msg = "WATCHDOG TIMEOUT phase=builds after build step $buildIdx (build budget exhausted)"
            Write-TimeoutLine $msg
            Write-PhaseLog "WATCHDOG: dotnet-dump not available; skipped"
            $script:FinalExitCode = 124
            throw "build watchdog"
        }
        $phaseName = "dotnet-build-$buildIdx"
        $exit = Invoke-DotNetWithWatchdog -PhaseName $phaseName -ArgumentList $buildArgs -PhaseCapMs $remainingMs
        if ($exit -eq 124) { throw "build watchdog" }
        if ($exit -ne 0) {
            $script:FinalExitCode = $exit
            throw "build failed"
        }
    }
    Write-PhaseLog "END phase=builds exit=0"
    Write-CiPhaseFirewallProbe -label "after-builds"

    $serverDll = Join-Path $repoRoot "tests/GameClub.Server.TestHost/bin/Release/net10.0/GameClub.Server.TestHost.dll"
    if (-not (Test-Path $serverDll)) { throw "GameClub.Server.TestHost.dll not found at $serverDll" }
    $serverLog = Join-Path $logDir "server-day-out.log"
    $serverErr = Join-Path $logDir "server-day-err.log"
    Start-CiGameClubTestHost -ServerDll $serverDll -BandArgs $script:ServerPcBandArgs -StdoutLog $serverLog -StderrLog $serverErr -ExpectedBand day -PhaseLabel "day-initial"
    Write-PhaseLog "END phase=server-ready exit=0"
    Write-CiPhaseFirewallProbe -label "after-server-ready"

    Write-Host "Listing LiveCheckpointServer tests (net10.0 + net48; must total 4)..."
    $liveNet10Names = @(Write-VstestPhaseList -phaseName "live-checkpoint-inventory" -framework "net10.0" -filterExpr "Category=LiveCheckpointServer")
    $liveNet48Names = @(Write-VstestPhaseList -phaseName "live-checkpoint-inventory" -framework "net48" -filterExpr "Category=LiveCheckpointServer")
    $liveAll = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($n in $liveNet10Names) { [void]$liveAll.Add($n.Trim()) }
    foreach ($n in $liveNet48Names) { [void]$liveAll.Add($n.Trim()) }
    Write-PhaseLog "LIVE_CHECKPOINT_TEST_COUNT $($liveAll.Count)"
    foreach ($name in ($liveAll | Sort-Object)) {
        Write-PhaseLog "LIVE_CHECKPOINT_TEST $name"
        Write-Host "LIVE_CHECKPOINT_TEST $name"
    }
    if ($liveAll.Count -ne 4) {
        throw "LiveCheckpointServer inventory expected 4 tests, saw $($liveAll.Count)"
    }

    Write-Host "Running windows server integration (net10.0, live server)..."
    Write-CiPhaseFirewallProbe -label "before-dotnet-test-net10-server"
    $serverFilter = "Category=WindowsServer&Category!=UbuntuG2Oracle"
    Write-VstestPhaseList -phaseName "dotnet-test-net10-server" -framework "net10.0" -filterExpr $serverFilter | Out-Null
    $serverTestArgs = @(
    "test", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj",
    "-c", "Release", "-f", "net10.0", "--no-build", "--nologo",
    "--filter", $serverFilter,
    "--logger", "trx;LogFileName=kiosk-windows-server.trx",
    "--results-directory", $artifactDir,
    "--blame-hang", "--blame-hang-timeout", "5m", "--blame-hang-dump-type", "none",
    "--logger", "console;verbosity=detailed"
)
    $serverDiag = Join-Path $logDir "vstest-diag-dotnet-test-net10-server.log"
    $serverTestExit = Invoke-DotNetWithWatchdog -PhaseName "dotnet-test-net10-server" -ArgumentList $serverTestArgs -PhaseCapMs (10 * 60 * 1000) -DiagLogPath $serverDiag
    $leaseBuildError = Publish-ServerLeaseBuildEvidence -phaseName "dotnet-test-net10-server" -TrxPath (Join-Path $artifactDir "kiosk-windows-server.trx")
    Write-CiPhaseFirewallProbe -label "after-dotnet-test-net10-server"
    if ($leaseBuildError) { throw $leaseBuildError }
    Assert-TestPhaseExit -PhaseName "dotnet-test-net10-server" -Exit $serverTestExit

    Write-Host "Running net48 LiveCheckpointServer tests (L17 apply-failure harness)..."
    Stop-OrphanedGuardAndKiosk -Reason "before_live_checkpoint_net48"
    $liveNet48Filter = "Category=LiveCheckpointServer"
    Write-VstestPhaseList -phaseName "dotnet-test-net48-live-checkpoint" -framework "net48" -filterExpr $liveNet48Filter | Out-Null
    $liveNet48Args = @(
        "test", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj",
        "-c", "Release", "-f", "net48", "--no-build", "--nologo",
        "--filter", $liveNet48Filter,
        "--logger", "trx;LogFileName=kiosk-windows-live-checkpoint-net48.trx",
        "--results-directory", $artifactDir,
        "--logger", "console;verbosity=detailed"
    )
    $liveNet48Out = Join-Path $logDir "live-checkpoint-net48-console.log"
    $liveNet48Err = Join-Path $logDir "live-checkpoint-net48-console-err.log"
    $liveNet48Exit = Invoke-DotNetWithWatchdog -PhaseName "dotnet-test-net48-live-checkpoint" -ArgumentList $liveNet48Args -PhaseCapMs (6 * 60 * 1000) -StdOutLog $liveNet48Out -StdErrLog $liveNet48Err
    Copy-Item $serverLog (Join-Path $logDir "live-checkpoint-server-out.log") -Force -ErrorAction SilentlyContinue
    Copy-Item $serverErr (Join-Path $logDir "live-checkpoint-server-err.log") -Force -ErrorAction SilentlyContinue
    Publish-ServerLeaseBuildEvidence -phaseName "dotnet-test-net48-live-checkpoint" -TrxPath (Join-Path $artifactDir "kiosk-windows-live-checkpoint-net48.trx")
    if (Test-Path $liveNet48Out) {
        Write-Host "--- live-checkpoint-net48-console.log (L17 net48) ---"
        Get-Content -Path $liveNet48Out -Encoding UTF8 | ForEach-Object { Write-Host $_ }
        Write-Host "--- end live-checkpoint-net48-console.log ---"
    }

    Assert-TestPhaseExit -PhaseName "dotnet-test-net48-live-checkpoint" -Exit $liveNet48Exit

    Write-Host "Reconciling live server after net10 server integration (handover/relaunch may leave orphan PIDs)..."
    $serverLog = Join-Path $logDir "server-day-out.log"
    $serverErr = Join-Path $logDir "server-day-err.log"
    Start-CiGameClubTestHost -ServerDll $serverDll -BandArgs $script:ServerPcBandArgs -StdoutLog $serverLog -StderrLog $serverErr -ExpectedBand day -PhaseLabel "day-post-net10"

    Write-Host "Running windows guard/wpf integration (net48, live server env)..."
    Write-Host "Restoring firewall after server integration (guard-wpf host timers need clean egress)..."
    Invoke-CiFirewallRestore -LogDir $logDir -FirewallLog $firewallLog
    Write-CiPhaseFirewallProbe -label "before-dotnet-test-net48-guard-wpf"
    $env:CHECKPOINT_SERVER_PC_BAND = 'night'
    Set-CiServerPcBandArgs -Mode night
    $serverNightLog = Join-Path $logDir "server-night-out.log"
    $serverNightErr = Join-Path $logDir "server-night-err.log"
    Start-CiGameClubTestHost -ServerDll $serverDll -BandArgs $script:ServerPcBandArgs -StdoutLog $serverNightLog -StderrLog $serverNightErr -ExpectedBand night -PhaseLabel "night-wpf"
    $nightWpfFilter = "FullyQualifiedName~wpf_remaining_time_matches_funded_session_special_band_night_line"
    Write-VstestPhaseList -phaseName "dotnet-test-net48-wpf-night" -framework "net48" -filterExpr $nightWpfFilter | Out-Null
    $nightWpfArgs = @(
        "test", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj",
        "-c", "Release", "-f", "net48", "--no-build", "--nologo",
        "--filter", $nightWpfFilter,
        "--logger", "console;verbosity=detailed"
    )
    $nightWpfExit = Invoke-DotNetWithWatchdog -PhaseName "dotnet-test-net48-wpf-night" -ArgumentList $nightWpfArgs -PhaseCapMs (5 * 60 * 1000)
    Publish-ServerLeaseBuildEvidence -phaseName "dotnet-test-net48-wpf-night" -TrxPath ""
    if ($nightWpfExit -ne 0) { $script:FinalExitCode = $nightWpfExit; throw "night WPF test failed" }
    $env:CHECKPOINT_SERVER_PC_BAND = $null
    Set-CiServerPcBandArgs -Mode day
    Start-CiGameClubTestHost -ServerDll $serverDll -BandArgs $script:ServerPcBandArgs -StdoutLog $serverLog -StderrLog $serverErr -ExpectedBand day -PhaseLabel "day-post-night-wpf"

    $hostTimerFailFilter = @(
        "FullyQualifiedName~host_boot_path_shows_no_server_response_when_heartbeats_fail",
        "FullyQualifiedName~host_late_connect_shows_no_server_response_when_heartbeats_fail",
        "FullyQualifiedName~host_operator_stop_lock_shows_no_server_response_when_heartbeats_fail",
        "FullyQualifiedName~host_open_session_no_display_shows_no_server_response_when_heartbeats_fail"
    ) -join "|"
    Write-Host "Running KioskHostStatusTimerTests heartbeat-fail scenarios (fresh testhost before guard-wpf bulk)..."
    Write-VstestPhaseList -phaseName "dotnet-test-net48-host-timer-fail" -framework "net48" -filterExpr $hostTimerFailFilter | Out-Null
    $hostTimerFailArgs = @(
        "test", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj",
        "-c", "Release", "-f", "net48", "--no-build", "--nologo",
        "--filter", $hostTimerFailFilter,
        "--logger", "console;verbosity=detailed"
    )
    $hostTimerFailExit = Invoke-DotNetWithWatchdog -PhaseName "dotnet-test-net48-host-timer-fail" -ArgumentList $hostTimerFailArgs -PhaseCapMs (5 * 60 * 1000)
    Assert-TestPhaseExit -PhaseName "dotnet-test-net48-host-timer-fail" -Exit $hostTimerFailExit

    $hostTimerFailExclude = @(
        "FullyQualifiedName!~host_boot_path_shows_no_server_response_when_heartbeats_fail",
        "FullyQualifiedName!~host_late_connect_shows_no_server_response_when_heartbeats_fail",
        "FullyQualifiedName!~host_operator_stop_lock_shows_no_server_response_when_heartbeats_fail",
        "FullyQualifiedName!~host_open_session_no_display_shows_no_server_response_when_heartbeats_fail"
    ) -join "&"
    $guardWpfFilter = "(Category=WindowsWpf&FullyQualifiedName!~wpf_remaining_time_matches_funded_session_special_band_night_line&$hostTimerFailExclude)|(Category=WindowsServer&Category!=LiveCheckpointServer&FullyQualifiedName!~wpf_remaining_time_matches_funded_session_special_band_night_line&$hostTimerFailExclude)"
    $guardWpfBaselineNames = @(Write-VstestPhaseList -phaseName "dotnet-test-net48-guard-wpf-inventory" -framework "net48" -filterExpr $guardWpfFilter)
    Write-Host "GUARD_WPF_INVENTORY baseline_count=$($guardWpfBaselineNames.Count) (R16 single-phase dotnet-test-net48-guard-wpf union)"

    # Fresh testhost per batch; each phase budget 5m (<= global clamp; avoids single ~271s effective cap on a long suite).
    $guardWpfHostTimerFilter = "(Category=WindowsWpf&FullyQualifiedName~KioskHostStatusTimerTests&FullyQualifiedName!~wpf_remaining_time_matches_funded_session_special_band_night_line&$hostTimerFailExclude)"
    $guardWpfWpfServerFilter = "((Category=WindowsWpf&FullyQualifiedName!~KioskHostStatusTimerTests&FullyQualifiedName!~wpf_remaining_time_matches_funded_session_special_band_night_line&$hostTimerFailExclude)|(Category=WindowsServer&Category!=LiveCheckpointServer&FullyQualifiedName!~wpf_remaining_time_matches_funded_session_special_band_night_line&$hostTimerFailExclude))"

    $guardHostTimerNames = @(Write-VstestPhaseList -phaseName "dotnet-test-net48-guard-wpf-host-timers" -framework "net48" -filterExpr $guardWpfHostTimerFilter)
    $guardWpfServerNames = @(Write-VstestPhaseList -phaseName "dotnet-test-net48-guard-wpf-wpf-server" -framework "net48" -filterExpr $guardWpfWpfServerFilter)

    $guardUnion = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($n in $guardHostTimerNames) { [void]$guardUnion.Add($n.Trim()) }
    foreach ($n in $guardWpfServerNames) { [void]$guardUnion.Add($n.Trim()) }
    $baselineSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($n in $guardWpfBaselineNames) { [void]$baselineSet.Add($n.Trim()) }
    $missingFromUnion = @($baselineSet | Where-Object { -not $guardUnion.Contains($_) })
    $extraInUnion = @($guardUnion | Where-Object { -not $baselineSet.Contains($_) })
    Write-Host "GUARD_WPF_INVENTORY union_count=$($guardUnion.Count) baseline_count=$($baselineSet.Count) missing_count=$($missingFromUnion.Count) extra_count=$($extraInUnion.Count)"
    if ($missingFromUnion.Count -gt 0) {
        foreach ($m in $missingFromUnion) { Write-Host "GUARD_WPF_INVENTORY_MISSING $m" }
        throw "guard-wpf split dropped tests (missing from union)"
    }
    if ($extraInUnion.Count -gt 0) {
        foreach ($e in $extraInUnion) { Write-Host "GUARD_WPF_INVENTORY_EXTRA $e" }
        throw "guard-wpf split added unexpected tests"
    }
    Write-Host "GUARD_WPF_INVENTORY diff=empty"

    $guardHostTimerArgs = @(
        "test", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj",
        "-c", "Release", "-f", "net48", "--no-build", "--nologo",
        "--filter", $guardWpfHostTimerFilter,
        "--logger", "trx;LogFileName=kiosk-windows-guard-host-timers.trx",
        "--results-directory", $artifactDir,
        "--blame-hang", "--blame-hang-timeout", "5m", "--blame-hang-dump-type", "none",
        "--logger", "console;verbosity=detailed"
    )
    $guardHostTimerDiag = Join-Path $logDir "vstest-diag-dotnet-test-net48-guard-wpf-host-timers.log"
    $guardHostTimerBudgetMs = 5 * 60 * 1000
    Write-Host "GUARD_WPF_PHASE_BUDGET phase=dotnet-test-net48-guard-wpf-host-timers cap_ms=$guardHostTimerBudgetMs"
    $guardHostTimerExit = Invoke-DotNetWithWatchdog -PhaseName "dotnet-test-net48-guard-wpf-host-timers" -ArgumentList $guardHostTimerArgs -PhaseCapMs $guardHostTimerBudgetMs -DiagLogPath $guardHostTimerDiag
    Publish-ServerLeaseBuildEvidence -phaseName "dotnet-test-net48-guard-wpf-host-timers" -TrxPath (Join-Path $artifactDir "kiosk-windows-guard-host-timers.trx")
    Assert-TestPhaseExit -PhaseName "dotnet-test-net48-guard-wpf-host-timers" -Exit $guardHostTimerExit

    $guardWpfServerArgs = @(
        "test", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj",
        "-c", "Release", "-f", "net48", "--no-build", "--nologo",
        "--filter", $guardWpfWpfServerFilter,
        "--logger", "trx;LogFileName=kiosk-windows-guard.trx",
        "--results-directory", $artifactDir,
        "--blame-hang", "--blame-hang-timeout", "5m", "--blame-hang-dump-type", "none",
        "--logger", "console;verbosity=detailed"
    )
    $guardDiag = Join-Path $logDir "vstest-diag-dotnet-test-net48-guard-wpf-wpf-server.log"
    $guardWpfServerBudgetMs = 5 * 60 * 1000
    Write-Host "GUARD_WPF_PHASE_BUDGET phase=dotnet-test-net48-guard-wpf-wpf-server cap_ms=$guardWpfServerBudgetMs"
    $guardWpfServerExit = Invoke-DotNetWithWatchdog -PhaseName "dotnet-test-net48-guard-wpf-wpf-server" -ArgumentList $guardWpfServerArgs -PhaseCapMs $guardWpfServerBudgetMs -DiagLogPath $guardDiag
    Publish-ServerLeaseBuildEvidence -phaseName "dotnet-test-net48-guard-wpf-wpf-server" -TrxPath (Join-Path $artifactDir "kiosk-windows-guard.trx")
    Assert-NightLineOnlyInNightPhase
    Write-CiPhaseFirewallProbe -label "after-dotnet-test-net48-guard-wpf"
    Assert-TestPhaseExit -PhaseName "dotnet-test-net48-guard-wpf-wpf-server" -Exit $guardWpfServerExit

    Write-Host "Running PcSpecialBandLiveRateCardOracleTests (net10)..."
    $oracleFilter = "FullyQualifiedName~PcSpecialBandLiveRateCardOracleTests"
    Write-VstestPhaseList -phaseName "dotnet-test-pc-special-band-oracle" -framework "net10.0" -filterExpr $oracleFilter | Out-Null
    $oracleArgs = @(
        "test", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj",
        "-c", "Release", "-f", "net10.0", "--no-build", "--nologo",
        "--filter", $oracleFilter,
        "--logger", "console;verbosity=detailed"
    )
    $oracleExit = Invoke-DotNetWithWatchdog -PhaseName "dotnet-test-pc-special-band-oracle" -ArgumentList $oracleArgs -PhaseCapMs (3 * 60 * 1000)
    Assert-TestPhaseExit -PhaseName "dotnet-test-pc-special-band-oracle" -Exit $oracleExit

    $wpfStabilityRuns = 0
    if ($env:WPF_STABILITY_RUNS -match '^\d+$') {
        $wpfStabilityRuns = [int]$env:WPF_STABILITY_RUNS
    }

    if ($wpfStabilityRuns -gt 0) {
        Write-Host "WPF stability runs=$wpfStabilityRuns execute in scripts/windows/ci-integration-wpf-stability.ps1 (not in this step)."
    }

    } # end full kiosk integration (not PcInternetFirewallOnly)
} catch {
    $errMsg = $_.Exception.Message
    $stack = $_.ScriptStackTrace
    Write-Host "CI integration error: $errMsg"
    if ($stack) { Write-Host $stack }
    Write-PhaseLog "CI integration error: $errMsg"
    if ($stack) { Write-PhaseLog $stack }
    if ($script:FinalExitCode -eq 0) { $script:FinalExitCode = 1 }
} finally {
    if (-not [string]::IsNullOrWhiteSpace($cancelFile)) {
        try { New-Item -Force -Path $cancelFile -ItemType File | Out-Null } catch { }
    }

    $leakGuard = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq "GameClub.Kiosk.Guard" })
    $leakKiosk = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq "GameClub.Kiosk" })
    Write-Host "CI_PROCESS_LEAK_GUARD count=$($leakGuard.Count)"
    Write-Host "CI_PROCESS_LEAK_KIOSK count=$($leakKiosk.Count)"
    if ($leakGuard.Count -gt 0 -or $leakKiosk.Count -gt 0) {
        foreach ($p in $leakGuard) { Stop-ProcessTree -rootProcId $p.Id }
        foreach ($p in $leakKiosk) { Stop-ProcessTree -rootProcId $p.Id }
        $leakGuard = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq "GameClub.Kiosk.Guard" })
        $leakKiosk = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq "GameClub.Kiosk" })
        Write-Host "CI_PROCESS_LEAK_GUARD_AFTER_KILL count=$($leakGuard.Count)"
        Write-Host "CI_PROCESS_LEAK_KIOSK_AFTER_KILL count=$($leakKiosk.Count)"
        if ($leakGuard.Count -gt 0 -or $leakKiosk.Count -gt 0) {
            if ($script:FinalExitCode -eq 0) { $script:FinalExitCode = 1 }
        }
    }

    Stop-CiManagedGameClubServerPids
    Assert-CiPipeAndProcessLeakFromLogs

    $watchdog = Join-Path $env:ProgramData "Checkpoint\guard\watchdog.log"
    if (Test-Path $watchdog) {
        try {
            Copy-Item $watchdog (Join-Path $artifactDir "watchdog.log") -Force -ErrorAction SilentlyContinue
        } catch { }
    }

    $checkpointEvidenceDir = Join-Path $env:ProgramData "Checkpoint\evidence"
    if (Test-Path $checkpointEvidenceDir) {
        try {
            Copy-Item $checkpointEvidenceDir (Join-Path $artifactDir "evidence") -Recurse -Force -ErrorAction SilentlyContinue
        } catch { }
    }
}

Write-PhaseLog "SCRIPT_EXIT FinalExitCode=$($script:FinalExitCode) LASTEXITCODE=$LASTEXITCODE"
Write-Host "ci-integration.ps1 completed."
exit $script:FinalExitCode
