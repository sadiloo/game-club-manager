param(
    [Parameter(Mandatory = $true)][string]$DeadlineUtcIso,
    [Parameter(Mandatory = $true)][string]$CancelFilePath,
    [Parameter(Mandatory = $true)][string]$MarkerPath,
    [Parameter(Mandatory = $true)][string]$SupervisorLogPath,
    [Parameter(Mandatory = $true)][string]$PhaseLogPath,
    [Parameter(Mandatory = $true)][string]$HeartbeatLogPath,
    [Parameter(Mandatory = $true)][string]$LogDir,
    [Parameter(Mandatory = $true)][string]$RepoRoot
)

$ErrorActionPreference = "Continue"

function Write-SupervisorLog([string]$message) {
    $line = "{0:u}  {1}" -f (Get-Date), $message
    try { Add-Content -Path $SupervisorLogPath -Value $line -Encoding UTF8 } catch { }
    Write-Host $line
}

function Get-LastLineOfFile([string]$path) {
    if (-not (Test-Path $path)) { return "" }
    try {
        $lines = Get-Content -Path $path -Tail 20 -ErrorAction SilentlyContinue | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        if (-not $lines -or $lines.Count -eq 0) { return "" }
        return [string]$lines[-1]
    } catch {
        return ""
    }
}

function Get-NewestVstestDiagLastLine([string]$dir) {
    if (-not (Test-Path $dir)) { return "" }
    try {
        $newest = Get-ChildItem -Path $dir -Filter "vstest-diag-*.log" -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if (-not $newest) { return "" }
        return Get-LastLineOfFile -path $newest.FullName
    } catch {
        return ""
    }
}

function Write-HeartbeatLine {
    $utc = (Get-Date).ToUniversalTime().ToString("u")
    $phaseTail = Get-LastLineOfFile -path $PhaseLogPath
    $diagTail = Get-NewestVstestDiagLastLine -dir $LogDir
    $line = "HEARTBEAT utc=$utc lastPhase=$phaseTail lastDiag=$diagTail"
    try { Add-Content -Path $HeartbeatLogPath -Value $line -Encoding UTF8 } catch { }
    Write-Host $line
}

function Stop-CiDeadmanProcessTargets {
    $selfId = $PID
    $protected = [System.Collections.Generic.HashSet[int]]::new()
    [void]$protected.Add($selfId)
    foreach ($runnerName in @("Runner.Worker", "Runner.Listener")) {
        Get-Process -Name $runnerName -ErrorAction SilentlyContinue | ForEach-Object {
            [void]$protected.Add($_.Id)
        }
    }
    Get-Process -ErrorAction SilentlyContinue | Where-Object {
        if ($protected.Contains($_.Id)) { return $false }
        $n = $_.ProcessName
        return ($n -eq "dotnet") -or ($n -like "testhost*") -or ($n -like "GameClub*") -or ($n -like "*Guard*") -or ($n -like "Kiosk*")
    } | ForEach-Object {
        & taskkill.exe /T /F /PID $_.Id 2>$null | Out-Null
    }
}

$deadlineUtc = [datetime]::Parse($DeadlineUtcIso, $null, [Globalization.DateTimeStyles]::RoundtripKind)
$firewallLog = Join-Path $LogDir "ci-firewall.log"

Write-SupervisorLog "deadman supervisor started deadline=$DeadlineUtcIso self=$PID"

. (Join-Path $PSScriptRoot "ci-firewall-restore.ps1")

while ($true) {
    if (Test-Path $CancelFilePath) {
        Write-SupervisorLog "deadman cancel file present; exiting 0"
        exit 0
    }

    if ((Get-Date).ToUniversalTime() -ge $deadlineUtc) {
        $lastPhase = Get-LastLineOfFile -path $PhaseLogPath
        $markerMsg = "DEADMAN fired $(Get-Date -Format u) lastPhase=$lastPhase"
        Write-SupervisorLog $markerMsg
        try { Add-Content -Path $MarkerPath -Value $markerMsg -Encoding UTF8 } catch { }
        try { Add-Content -Path $PhaseLogPath -Value $markerMsg -Encoding UTF8 } catch { }
        try {
            Save-CiFirewallInitialState -LogDir $LogDir | Out-Null
            Invoke-CiFirewallRestore -LogDir $LogDir -FirewallLog $firewallLog
            Write-CiFirewallEvidence -Path $firewallLog -Label "deadman"
            Write-CiEgressToLogs -FirewallLog $firewallLog -WritePhase { param($l) Add-Content -Path $PhaseLogPath -Value ("{0:u}  {1}" -f (Get-Date), $l) -Encoding UTF8 }
        } catch { }
        Stop-CiDeadmanProcessTargets
        exit 124
    }

    Write-HeartbeatLine
    Start-Sleep -Seconds 30
}
