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
$phaseLog = Join-Path $logDir "ci-phase.log"
$timeoutMarker = Join-Path $artifactDir "ci-timeout-marker.log"
$heartbeatLog = Join-Path $logDir "ci-heartbeat.log"
$firewallLog = Join-Path $logDir "ci-firewall.log"
$cancelFile = Join-Path $logDir "deadman.cancel"

$script:GlobalDeadline = (Get-Date).AddMinutes(16)
$script:FinalExitCode = 0
$script:NightLineVstestPhases = @{}
$nightLineToken = "wpf_remaining_time_matches_funded_session_special_band_night_line"

function Write-PhaseLog([string]$message) {
    $line = "{0:u}  {1}" -f (Get-Date), $message
    Add-Content -Path $phaseLog -Value $line -Encoding UTF8
}

function Write-TimeoutLine([string]$message) {
    $line = "{0:u}  {1}" -f (Get-Date), $message
    Add-Content -Path $phaseLog -Value $line -Encoding UTF8
    Add-Content -Path $timeoutMarker -Value $line -Encoding UTF8
    Write-Host $message
}

function Get-EffectiveTimeoutMs([int]$phaseCapMs) {
    $remaining = ($script:GlobalDeadline - (Get-Date)).TotalMilliseconds - 30000
    if ($remaining -lt 0) { $remaining = 0 }
    $cap = [long]$phaseCapMs
    if ($remaining -lt $cap) { return [int][Math]::Max(0, [int]$remaining) }
    return [int]$cap
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
    Write-PhaseLog "PHASE boundary $label"
    Write-CiFirewallEvidence -Path $firewallLog -Label $label
    Write-CiEgressToLogs -FirewallLog $firewallLog -WritePhase { param($line) Write-PhaseLog $line }
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
    $logPaths = @(
        (Join-Path $logDir "server-day-out.log"),
        (Join-Path $logDir "server-night-out.log"),
        (Join-Path $logDir "server-day-err.log"),
        (Join-Path $logDir "server-night-err.log")
    )
    foreach ($logPath in $logPaths) {
        if (-not (Test-Path $logPath)) { continue }
        $dest = Join-Path $artifactDir ([IO.Path]::GetFileName($logPath))
        Copy-Item -Path $logPath -Destination $dest -Force
        $lines = @(Select-String -Path $logPath -Pattern '^LEASE_BUILD\b' -ErrorAction SilentlyContinue)
        foreach ($line in $lines) {
            Write-PhaseLog "LEASE_BUILD phase=$phaseName sourceLog=$([IO.Path]::GetFileName($logPath)) $($line.Line)"
            Write-Host $line.Line
        }
    }

    if ($phaseName -ne 'dotnet-test-net10-server') { return }
    if (-not (Test-Path $TrxPath)) { return }
    [xml]$trx = Get-Content $TrxPath
    $ranReconnect = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.testName -like '*live_server_kiosk_reconnects_when_server_unreachable_at_start*' })
    if ($ranReconnect.Count -eq 0) { return }
    $dayLog = Join-Path $logDir "server-day-out.log"
    if (-not (Test-Path $dayLog)) { throw 'LEASE_BUILD missing (server-day-out.log not found)' }
    $leaseLines = @(Select-String -Path $dayLog -Pattern '^LEASE_BUILD\b' -ErrorAction SilentlyContinue)
    if ($leaseLines.Count -eq 0) {
        throw 'LEASE_BUILD missing'
    }
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
        [string]$DiagLogPath = ""
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
    $proc = Start-Process -FilePath "dotnet" -ArgumentList $dotnetArgs -PassThru -NoNewWindow -WorkingDirectory $repoRoot
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
    $env:GAMECLUB_TEST_DATABASE = "Host=localhost;Port=5432;Database=gameclub_test;Username=postgres;Password=postgres;SSL Mode=Disable"
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
Write-PhaseLog "START deadman pid=$($supervisorProc.Id) deadline=$deadmanDeadlineUtc"

Write-CiPhaseFirewallProbe -label "pre-integration"

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
    if ($env:RUNNER_ROOT) {
        $listener = Join-Path $env:RUNNER_ROOT "bin\Runner.Listener.exe"
        $worker = Join-Path $env:RUNNER_ROOT "bin\Runner.Worker.exe"
        if ((Test-Path $listener) -and (Test-Path $worker)) {
            $env:CHECKPOINT_INTERNET_EXTRA_ALLOW = "$listener;$worker"
        }
    }

    $env:PGPASSWORD = "postgres"

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
    Publish-ServerLeaseBuildEvidence -phaseName "dotnet-test-net10-server" -TrxPath (Join-Path $artifactDir "kiosk-windows-server.trx")
    Write-CiPhaseFirewallProbe -label "after-dotnet-test-net10-server"
    if ($serverTestExit -eq 124) { throw "server test watchdog" }

    Write-Host "Running net48 LiveCheckpointServer tests (L17 apply-failure harness)..."
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
    $liveNet48Exit = Invoke-DotNetWithWatchdog -PhaseName "dotnet-test-net48-live-checkpoint" -ArgumentList $liveNet48Args -PhaseCapMs (6 * 60 * 1000)
    Publish-ServerLeaseBuildEvidence -phaseName "dotnet-test-net48-live-checkpoint" -TrxPath (Join-Path $artifactDir "kiosk-windows-live-checkpoint-net48.trx")
    if ($liveNet48Exit -ne 0 -and $script:FinalExitCode -eq 0) { $script:FinalExitCode = $liveNet48Exit }
    if ($liveNet48Exit -eq 124) { throw "live checkpoint net48 watchdog" }

    Write-Host "Reconciling live server after net10 server integration (handover/relaunch may leave orphan PIDs)..."
    $serverLog = Join-Path $logDir "server-day-out.log"
    $serverErr = Join-Path $logDir "server-day-err.log"
    Start-CiGameClubTestHost -ServerDll $serverDll -BandArgs $script:ServerPcBandArgs -StdoutLog $serverLog -StderrLog $serverErr -ExpectedBand day -PhaseLabel "day-post-net10"

    Write-Host "Running windows guard/wpf integration (net48, live server env)..."
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

    $guardWpfFilter = "(Category=WindowsWpf&FullyQualifiedName!~wpf_remaining_time_matches_funded_session_special_band_night_line)|(Category=WindowsServer&Category!=LiveCheckpointServer&FullyQualifiedName!~wpf_remaining_time_matches_funded_session_special_band_night_line)"
    Write-VstestPhaseList -phaseName "dotnet-test-net48-guard-wpf" -framework "net48" -filterExpr $guardWpfFilter | Out-Null
    $guardTestArgs = @(
        "test", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj",
        "-c", "Release", "-f", "net48", "--no-build", "--nologo",
        "--filter", $guardWpfFilter,
        "--logger", "trx;LogFileName=kiosk-windows-guard.trx",
        "--results-directory", $artifactDir,
        "--blame-hang", "--blame-hang-timeout", "5m", "--blame-hang-dump-type", "none",
        "--logger", "console;verbosity=detailed"
    )
    $guardDiag = Join-Path $logDir "vstest-diag-dotnet-test-net48-guard-wpf.log"
    $guardTestExit = Invoke-DotNetWithWatchdog -PhaseName "dotnet-test-net48-guard-wpf" -ArgumentList $guardTestArgs -PhaseCapMs (8 * 60 * 1000) -DiagLogPath $guardDiag
    Publish-ServerLeaseBuildEvidence -phaseName "dotnet-test-net48-guard-wpf" -TrxPath (Join-Path $artifactDir "kiosk-windows-guard.trx")
    Assert-NightLineOnlyInNightPhase
    Write-CiPhaseFirewallProbe -label "after-dotnet-test-net48-guard-wpf"
    if ($guardTestExit -eq 124) { throw "guard test watchdog" }

    if ($serverTestExit -ne 0) { $script:FinalExitCode = $serverTestExit }
    if ($guardTestExit -ne 0 -and $script:FinalExitCode -eq 0) { $script:FinalExitCode = $guardTestExit }

    Write-Host "Running PC internet firewall integration (Category=PcInternetFirewall)..."
    Write-CiPhaseFirewallProbe -label "before-dotnet-test-pc-internet-firewall"
    $fwFilter = "Category=PcInternetFirewall"
    Write-VstestPhaseList -phaseName "dotnet-test-pc-internet-firewall" -framework "net48" -filterExpr $fwFilter | Out-Null
    $fwTestArgs = @(
        "test", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj",
        "-c", "Release", "-f", "net48", "--no-build", "--nologo",
        "--filter", $fwFilter,
        "--logger", "trx;LogFileName=kiosk-windows-pc-internet-firewall.trx",
        "--results-directory", $artifactDir,
        "--logger", "console;verbosity=detailed"
    )
    $fwTestExit = Invoke-DotNetWithWatchdog -PhaseName "dotnet-test-pc-internet-firewall" -ArgumentList $fwTestArgs -PhaseCapMs (5 * 60 * 1000)
    Publish-ServerLeaseBuildEvidence -phaseName "dotnet-test-pc-internet-firewall" -TrxPath (Join-Path $artifactDir "kiosk-windows-pc-internet-firewall.trx")
    Write-CiPhaseFirewallProbe -label "after-dotnet-test-pc-internet-firewall"
    if ($fwTestExit -ne 0 -and $script:FinalExitCode -eq 0) { $script:FinalExitCode = $fwTestExit }

    $wpfStabilityRuns = 0
    if ($env:WPF_STABILITY_RUNS -match '^\d+$') {
        $wpfStabilityRuns = [int]$env:WPF_STABILITY_RUNS
    }

    if ($wpfStabilityRuns -gt 0) {
        Write-Host "WPF stability runs=$wpfStabilityRuns execute in scripts/windows/ci-integration-wpf-stability.ps1 (not in this step)."
    }
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

    Stop-CiManagedGameClubServerPids

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
