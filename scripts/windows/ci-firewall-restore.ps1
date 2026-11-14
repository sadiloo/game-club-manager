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
    $profiles = @()
    try {
        foreach ($prof in Get-NetFirewallProfile -ErrorAction Continue) {
            $profiles += [ordered]@{
                Name                  = $prof.Name
                Enabled               = $prof.Enabled
                DefaultInboundAction  = $prof.DefaultInboundAction.ToString()
                DefaultOutboundAction = $prof.DefaultOutboundAction.ToString()
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

    try {
        foreach ($rule in Get-CiMatchingFirewallRules) {
            try {
                Remove-NetFirewallRule -Name $rule.Name -ErrorAction Continue | Out-Null
            } catch { }
        }
    } catch { }

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
