param(
    [switch]$Evidence,
    [switch]$Restore
)

$ErrorActionPreference = "Continue"

$script:CiFirewallRulePatterns = @("Checkpoint*", "GameClub*", "Guard*", "Kiosk*")

function Get-CiArtifactLogDir {
    param([string]$LogDir)
    if (-not [string]::IsNullOrWhiteSpace($LogDir)) {
        return $LogDir
    }
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
    $artifactDir = Join-Path $env:GITHUB_WORKSPACE "artifacts"
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE)) {
        $artifactDir = Join-Path $repoRoot "artifacts"
    }
    return (Join-Path $artifactDir "kiosk-windows-logs")
}

function Get-CiFirewallProfilesSnapshot {
    param([string]$PolicyStore = $null)
    $profiles = @()
    try {
        $args = @{}
        if ($PolicyStore) { $args["PolicyStore"] = $PolicyStore }
        foreach ($profName in @('Domain','Private','Public')) {
            $p = Get-NetFirewallProfile -Name $profName @args -ErrorAction SilentlyContinue
            if (-not $p) { continue }
            $profiles += [ordered]@{
                Name                  = $profName
                Enabled               = $p.Enabled
                DefaultInboundAction  = $p.DefaultInboundAction.ToString()
                DefaultOutboundAction = $p.DefaultOutboundAction.ToString()
            }
        }
    } catch {
        Write-Host "Get-CiFirewallProfilesSnapshot failed: $($_.Exception.Message)"
    }
    return $profiles
}

function Save-CiFirewallInitialState {
    param([string]$LogDir)
    $dir = Get-CiArtifactLogDir -LogDir $LogDir
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $path = Join-Path $dir "firewall-initial.json"
    if (Test-Path $path) {
        return $path
    }
    try {
        $payload = @{
            CapturedUtc = (Get-Date).ToUniversalTime().ToString("o")
            Profiles    = Get-CiFirewallProfilesSnapshot
            LocalhostProfiles = (Get-CiFirewallProfilesSnapshot -PolicyStore "localhost")
        }
        $payload | ConvertTo-Json -Depth 6 | Set-Content -Path $path -Encoding UTF8
    } catch {
        Write-Host "Save-CiFirewallInitialState failed: $($_.Exception.Message)"
    }
    return $path
}

function Get-CiMatchingFirewallRules {
    $rules = @()
    try {
        $all = Get-NetFirewallRule -ErrorAction Continue
        foreach ($rule in $all) {
            $match = $false
            foreach ($pat in $script:CiFirewallRulePatterns) {
                if ($rule.DisplayName -like $pat -or $rule.Group -like $pat) {
                    $match = $true
                    break
                }
            }
            if ($match) {
                $rules += $rule
            }
        }
    } catch {
        Write-Host "Get-CiMatchingFirewallRules failed: $($_.Exception.Message)"
    }
    return $rules
}

function Test-CiTcpConnect {
    param(
        [string]$TargetHost,
        [int]$Port,
        [int]$TimeoutMs = 3000
    )
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $connectTask = $client.ConnectAsync($TargetHost, $Port)
        $ok = $connectTask.Wait($TimeoutMs)
        try { $client.Close() } catch { }
        return $ok
    } catch {
        return $false
    }
}

function Add-CiFirewallInternetOffDiagnostics {
    param([string]$Path)

    try {
        $mps = Get-Service -Name MpsSvc -ErrorAction SilentlyContinue
        $mpsLine = "CI_FIREWALL MpsSvc status=$($mps.Status)"
        Add-Content -Path $Path -Value $mpsLine -Encoding UTF8
        Write-Host $mpsLine
    } catch {
        $fail = "CI_FIREWALL MpsSvc status=unknown error=$($_.Exception.Message)"
        Add-Content -Path $Path -Value $fail -Encoding UTF8
        Write-Host $fail
    }

    try {
        foreach ($profName in @('Domain', 'Private', 'Public')) {
            $active = Get-NetFirewallProfile -Name $profName -ErrorAction SilentlyContinue
            if ($active) {
                $line = "CI_FIREWALL active_profile name=$profName enabled=$($active.Enabled) in=$($active.DefaultInboundAction) out=$($active.DefaultOutboundAction)"
                Add-Content -Path $Path -Value $line -Encoding UTF8
                Write-Host $line
            }

            $local = Get-NetFirewallProfile -PolicyStore localhost -Name $profName -ErrorAction SilentlyContinue
            if ($local) {
                $line = "CI_FIREWALL localhost_profile name=$profName enabled=$($local.Enabled) in=$($local.DefaultInboundAction) out=$($local.DefaultOutboundAction)"
                Add-Content -Path $Path -Value $line -Encoding UTF8
                Write-Host $line
            }
        }
    } catch {
        Write-Host "Add-CiFirewallInternetOffDiagnostics profiles failed: $($_.Exception.Message)"
    }

    try {
        foreach ($ncp in @(Get-NetConnectionProfile -ErrorAction SilentlyContinue)) {
            $line = "CI_FIREWALL ConnectionProfile name=$($ncp.Name) interface=$($ncp.InterfaceAlias) category=$($ncp.NetworkCategory)"
            Add-Content -Path $Path -Value $line -Encoding UTF8
            Write-Host $line
        }
    } catch {
        Write-Host "Add-CiFirewallInternetOffDiagnostics ConnectionProfile failed: $($_.Exception.Message)"
    }

    try {
        $guardRules = @(Get-NetFirewallRule -PolicyStore localhost -Group "Checkpoint Internet Off" -ErrorAction SilentlyContinue)
        $ruleLine = "CI_FIREWALL guard_rule_count localhost_group=Checkpoint Internet Off count=$($guardRules.Count)"
        Add-Content -Path $Path -Value $ruleLine -Encoding UTF8
        Write-Host $ruleLine
        foreach ($rule in $guardRules) {
            $detail = "CI_FIREWALL guard_rule name=$($rule.DisplayName) enabled=$($rule.Enabled) dir=$($rule.Direction) action=$($rule.Action)"
            Add-Content -Path $Path -Value $detail -Encoding UTF8
            Write-Host $detail
        }
    } catch {
        Write-Host "Add-CiFirewallInternetOffDiagnostics guard rules failed: $($_.Exception.Message)"
    }

    $statePath = Join-Path $env:ProgramData "Checkpoint\guard\internet-state.json"
    if (Test-Path $statePath) {
        try {
            $raw = Get-Content -Path $statePath -Raw -ErrorAction Stop
            Add-Content -Path $Path -Value "CI_FIREWALL guard_state_path=$statePath payload=$raw" -Encoding UTF8
            Write-Host "CI_FIREWALL guard_state_path=$statePath"
        } catch {
            Write-Host "Add-CiFirewallInternetOffDiagnostics guard state read failed: $($_.Exception.Message)"
        }
    } else {
        $missing = "CI_FIREWALL guard_state_path=$statePath present=false"
        Add-Content -Path $Path -Value $missing -Encoding UTF8
        Write-Host $missing
    }
}

function Add-CiCheckpointPublicEgressEvidence {
    param(
        [string]$Path,
        [string]$Label = "baseline"
    )

    Add-CiFirewallInternetOffDiagnostics -Path $Path

    $guardRuleCount = 0
    try {
        $guardRuleCount = @(Get-NetFirewallRule -PolicyStore localhost -Group "Checkpoint Internet Off" -ErrorAction SilentlyContinue).Count
    } catch {
        $guardRuleCount = -1
    }
    $statePath = Join-Path $env:ProgramData "Checkpoint\guard\internet-state.json"
    $statePresent = Test-Path $statePath

    $checkpointHost = "127.0.0.1"
    $checkpointPort = 5225
    $serverUrl = $env:CHECKPOINT_SERVER_URL
    if (-not [string]::IsNullOrWhiteSpace($serverUrl)) {
        try {
            $uri = [Uri]$serverUrl
            if ($uri.Host) { $checkpointHost = $uri.Host }
            if ($uri.Port -gt 0) { $checkpointPort = $uri.Port }
        } catch {
        }
    }

    $publicBlocked = -not (Test-CiTcpConnect -TargetHost "1.1.1.1" -Port 443 -TimeoutMs 2000)
    $serverReachable = Test-CiTcpConnect -TargetHost $checkpointHost -Port $checkpointPort -TimeoutMs 2000
    $serverProcessAlive = $false
    $serverPid = $env:GAMECLUB_SERVER_PID
    if (-not [string]::IsNullOrWhiteSpace($serverPid) -and $serverPid -match '^\d+$') {
        $serverProcessAlive = $null -ne (Get-Process -Id ([int]$serverPid) -ErrorAction SilentlyContinue)
    }

    $loopbackNote = if (-not $serverReachable -and -not $serverProcessAlive) {
        "expected_server_stopped"
    } elseif (-not $serverReachable -and $serverProcessAlive) {
        "unexpected_loopback_blocked_while_server_alive"
    } else {
        "ok"
    }

    $publicLine = "CI_EGRESS label=$Label guard_rules=$guardRuleCount state_present=$statePresent public=1.1.1.1:443 blocked=$publicBlocked"
    $serverLine = "CI_EGRESS label=$Label checkpoint=${checkpointHost}:${checkpointPort} reachable=$serverReachable server_process_alive=$serverProcessAlive loopback_note=$loopbackNote guard_rules=$guardRuleCount state_present=$statePresent"
    try {
        Add-Content -Path $Path -Value $publicLine -Encoding UTF8
        Add-Content -Path $Path -Value $serverLine -Encoding UTF8
    } catch {
        Write-Host "Add-CiCheckpointPublicEgressEvidence failed: $($_.Exception.Message)"
    }
    Write-Host $publicLine
    Write-Host $serverLine
}

function Write-CiFirewallEvidence {
    param(
        [string]$Path,
        [string]$Label = "evidence"
    )
    try {
        $line = "{0:u}  FIREWALL evidence label={1}" -f (Get-Date), $Label
        Add-Content -Path $Path -Value $line -Encoding UTF8
        foreach ($prof in Get-CiFirewallProfilesSnapshot) {
            $profLine = "  profile=$($prof.Name) enabled=$($prof.Enabled) in=$($prof.DefaultInboundAction) out=$($prof.DefaultOutboundAction)"
            Add-Content -Path $Path -Value $profLine -Encoding UTF8
        }
        foreach ($rule in Get-CiMatchingFirewallRules) {
            $ruleLine = "  rule name=$($rule.DisplayName) group=$($rule.Group) enabled=$($rule.Enabled) dir=$($rule.Direction) action=$($rule.Action)"
            Add-Content -Path $Path -Value $ruleLine -Encoding UTF8
        }
        Add-CiCheckpointPublicEgressEvidence -Path $Path -Label $Label
    } catch {
        Write-Host "Write-CiFirewallEvidence failed: $($_.Exception.Message)"
    }
}

function Get-CiEgressProbeHosts {
    $hosts = @("github.com", "api.github.com")
    try {
        [void][System.Net.Dns]::GetHostAddresses("pipelines.actions.githubusercontent.com")
        $hosts += "pipelines.actions.githubusercontent.com"
    } catch {
    }
    return $hosts
}

function Test-CiGitHubEgress {
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($targetHost in Get-CiEgressProbeHosts) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $ok = $false
        try {
            $client = New-Object System.Net.Sockets.TcpClient
            $connectTask = $client.ConnectAsync($targetHost, 443)
            $ok = $connectTask.Wait(5000)
            try { $client.Close() } catch { }
        } catch {
            $ok = $false
        }
        $sw.Stop()
        $status = if ($ok) { "ok" } else { "FAIL" }
        $lines.Add("EGRESS ${targetHost}:443 $status $($sw.ElapsedMilliseconds)ms")
    }
    return $lines
}

function Write-CiEgressToLogs {
    param(
        [string]$FirewallLog,
        [scriptblock]$WritePhase
    )
    foreach ($line in (Test-CiGitHubEgress)) {
        try {
            Add-Content -Path $FirewallLog -Value ("{0:u}  {1}" -f (Get-Date), $line) -Encoding UTF8
        } catch { }
        if ($null -ne $WritePhase) {
            & $WritePhase $line
        }
    }
}

function Invoke-CiFirewallRestore {
    param(
        [string]$LogDir,
        [string]$FirewallLog
    )
    $dir = Get-CiArtifactLogDir -LogDir $LogDir
    $initialPath = Join-Path $dir "firewall-initial.json"
    $savedProfiles = @{}
    if (Test-Path $initialPath) {
        try {
            $json = Get-Content $initialPath -Raw | ConvertFrom-Json
            foreach ($prof in $json.Profiles) {
                $savedProfiles[$prof.Name] = $prof
            }
        } catch {
            Write-Host "Invoke-CiFirewallRestore: could not read $initialPath"
        }
    }

    try {
        Add-Content -Path $FirewallLog -Value ("{0:u}  FIREWALL restore BEGIN" -f (Get-Date)) -Encoding UTF8
        Write-CiFirewallEvidence -Path $FirewallLog -Label "before-restore"
    } catch { }

    $guardDir = Join-Path $env:ProgramData "Checkpoint\guard"
    try {
        if (Test-Path $guardDir) {
            Get-ChildItem -Path $guardDir -Filter "internet*.json" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        }
    } catch { }

    try {
        foreach ($rule in Get-CiMatchingFirewallRules) {
            try {
                Remove-NetFirewallRule -Name $rule.Name -ErrorAction Continue | Out-Null
            } catch { }
        }
        try {
            Get-NetFirewallRule -PolicyStore localhost -Group "Checkpoint Internet Off" -ErrorAction SilentlyContinue |
                Remove-NetFirewallRule -PolicyStore localhost -ErrorAction SilentlyContinue
        } catch { }
    } catch { }

    $localhostProfiles = @{}
    if (Test-Path $initialPath) {
        try {
            $json = Get-Content $initialPath -Raw | ConvertFrom-Json
            foreach ($prof in $json.LocalhostProfiles) {
                $localhostProfiles[$prof.Name] = $prof
            }
        } catch { }
    }

    foreach ($profName in @("Domain", "Private", "Public")) {
        try {
            $current = Get-NetFirewallProfile -Name $profName -ErrorAction Continue
            if (-not $current) { continue }
            $saved = $savedProfiles[$profName]
            $targetOutbound = "Allow"
            if ($saved -and $saved.DefaultOutboundAction) {
                $targetOutbound = [string]$saved.DefaultOutboundAction
            }
            if ($current.DefaultOutboundAction.ToString() -ne $targetOutbound) {
                Set-NetFirewallProfile -Name $profName -DefaultOutboundAction $targetOutbound -ErrorAction Continue | Out-Null
            }
            if ($saved -and $saved.DefaultInboundAction) {
                $targetInbound = [string]$saved.DefaultInboundAction
                if ($current.DefaultInboundAction.ToString() -ne $targetInbound) {
                    Set-NetFirewallProfile -Name $profName -DefaultInboundAction $targetInbound -ErrorAction Continue | Out-Null
                }
            }
        } catch {
            Write-Host "Invoke-CiFirewallRestore profile $profName failed: $($_.Exception.Message)"
        }
    }

    foreach ($profName in @("Domain", "Private", "Public")) {
        try {
            $saved = $localhostProfiles[$profName]
            if (-not $saved) { continue }
            $targetOutbound = if ($saved.DefaultOutboundAction) { [string]$saved.DefaultOutboundAction } else { "NotConfigured" }
            Set-NetFirewallProfile -PolicyStore localhost -Name $profName -DefaultOutboundAction $targetOutbound -ErrorAction SilentlyContinue | Out-Null
            if ($saved.DefaultInboundAction) {
                Set-NetFirewallProfile -PolicyStore localhost -Name $profName -DefaultInboundAction ([string]$saved.DefaultInboundAction) -ErrorAction SilentlyContinue | Out-Null
            }
            if ($null -ne $saved.Enabled) {
                Set-NetFirewallProfile -PolicyStore localhost -Name $profName -Enabled ([bool]$saved.Enabled) -ErrorAction SilentlyContinue | Out-Null
            }
        } catch { }
    }

    try {
        Write-CiFirewallEvidence -Path $FirewallLog -Label "after-restore"
        Add-Content -Path $FirewallLog -Value ("{0:u}  FIREWALL restore END" -f (Get-Date)) -Encoding UTF8
    } catch { }
}

if ($Evidence -or $Restore) {
    $logDir = Get-CiArtifactLogDir -LogDir ""
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $firewallLog = Join-Path $logDir "ci-firewall.log"
    Save-CiFirewallInitialState -LogDir $logDir
    if ($Restore) {
        Invoke-CiFirewallRestore -LogDir $logDir -FirewallLog $firewallLog
    }
    if ($Evidence) {
        Write-CiFirewallEvidence -Path $firewallLog -Label "workflow-reset"
        Write-CiEgressToLogs -FirewallLog $firewallLog -WritePhase $null
        foreach ($line in (Test-CiGitHubEgress)) {
            Write-Host $line
        }
    }
}
