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

$serverProc = $null
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

    $serverDll = Join-Path $repoRoot "src/GameClub.Server/bin/Release/net10.0/GameClub.Server.dll"
    if (-not (Test-Path $serverDll)) { throw "GameClub.Server.dll not found at $serverDll" }
    $serverLog = Join-Path $logDir "server-out.log"
    $serverErr = Join-Path $logDir "server-err.log"
    Write-PhaseLog "START phase=server-start"
    $serverProc = Start-Process -FilePath "dotnet" -ArgumentList @("exec", $serverDll) -PassThru -NoNewWindow -RedirectStandardOutput $serverLog -RedirectStandardError $serverErr -WorkingDirectory $repoRoot
    $null = $serverProc.Handle
    $env:GAMECLUB_SERVER_PID = $serverProc.Id
    $env:GAMECLUB_SERVER_DLL = $serverDll

    Write-PhaseLog "START phase=server-ready"
    $readyCapMs = 2 * 60 * 1000
    $readyDeadline = (Get-Date).AddMilliseconds([double](Get-EffectiveTimeoutMs -phaseCapMs $readyCapMs))
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
        Write-TimeoutLine "WATCHDOG TIMEOUT phase=server-ready after 120s"
        Write-PhaseLog "WATCHDOG: dotnet-dump not available; skipped"
        if ($serverProc) {
            Write-ProcessTree -rootProcId $serverProc.Id
            Stop-ProcessTree -rootProcId $serverProc.Id
        }
        $script:FinalExitCode = 124
        throw "server not ready"
    }
    Write-PhaseLog "END phase=server-ready exit=0"
    Write-CiPhaseFirewallProbe -label "after-server-ready"

    Write-Host "Running windows server integration (net10.0, live server)..."
    Write-CiPhaseFirewallProbe -label "before-dotnet-test-net10-server"
    $serverTestArgs = @(
    "test", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj",
    "-c", "Release", "-f", "net10.0", "--no-build", "--nologo",
    "--filter", "Category!=WindowsGuard",
    "--logger", "trx;LogFileName=kiosk-windows-server.trx",
    "--results-directory", $artifactDir,
    "--blame-hang", "--blame-hang-timeout", "5m", "--blame-hang-dump-type", "none",
    "--logger", "console;verbosity=detailed"
)
    $serverDiag = Join-Path $logDir "vstest-diag-dotnet-test-net10-server.log"
    $serverTestExit = Invoke-DotNetWithWatchdog -PhaseName "dotnet-test-net10-server" -ArgumentList $serverTestArgs -PhaseCapMs (6 * 60 * 1000) -DiagLogPath $serverDiag
    Write-CiPhaseFirewallProbe -label "after-dotnet-test-net10-server"
    if ($serverTestExit -eq 124) { throw "server test watchdog" }

    Write-Host "Running windows guard/wpf integration (net48, live server env)..."
    Write-CiPhaseFirewallProbe -label "before-dotnet-test-net48-guard-wpf"
    $guardTestArgs = @(
        "test", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj",
        "-c", "Release", "-f", "net48", "--no-build", "--nologo",
        "--filter", "Category!=WindowsGuard",
        "--logger", "trx;LogFileName=kiosk-windows-guard.trx",
        "--results-directory", $artifactDir,
        "--blame-hang", "--blame-hang-timeout", "5m", "--blame-hang-dump-type", "none",
        "--logger", "console;verbosity=detailed"
    )
    $guardDiag = Join-Path $logDir "vstest-diag-dotnet-test-net48-guard-wpf.log"
    $guardTestExit = Invoke-DotNetWithWatchdog -PhaseName "dotnet-test-net48-guard-wpf" -ArgumentList $guardTestArgs -PhaseCapMs (8 * 60 * 1000) -DiagLogPath $guardDiag
    Write-CiPhaseFirewallProbe -label "after-dotnet-test-net48-guard-wpf"
    if ($guardTestExit -eq 124) { throw "guard test watchdog" }

    if ($serverTestExit -ne 0) { $script:FinalExitCode = $serverTestExit }
    if ($guardTestExit -ne 0 -and $script:FinalExitCode -eq 0) { $script:FinalExitCode = $guardTestExit }
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
    if ($null -ne $serverProc -and -not $serverProc.HasExited) {
        try {
            Stop-ProcessTree -rootProcId $serverProc.Id
        } catch { }
    }

    $watchdog = Join-Path $env:ProgramData "Checkpoint\guard\watchdog.log"
    if (Test-Path $watchdog) {
        try {
            Copy-Item $watchdog (Join-Path $artifactDir "watchdog.log") -Force -ErrorAction SilentlyContinue
        } catch { }
    }

    $evidence = Join-Path $env:ProgramData "Checkpoint\evidence"
    if (Test-Path $evidence) {
        try {
            Copy-Item $evidence (Join-Path $artifactDir "evidence") -Recurse -Force -ErrorAction SilentlyContinue
        } catch { }
    }
}

if ($script:FinalExitCode -ne 0) {
    exit $script:FinalExitCode
}

Write-Host "ci-integration.ps1 completed."
