$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Get-CheckpointSetupUiText.ps1")
. (Join-Path $PSScriptRoot "..\..\ci\checkpoint-setup-exit-codes.ps1")

function Write-AcceptanceHarnessTrace {
    param(
        [string] $Label,
        [scriptblock] $Action
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "acceptance-harness: $([DateTime]::UtcNow.ToString('o')) BEGIN $Label"
    [Console]::Out.Flush()
    try {
        return & $Action
    } finally {
        Write-Host "acceptance-harness: $([DateTime]::UtcNow.ToString('o')) END $Label elapsedMs=$($sw.ElapsedMilliseconds)"
        [Console]::Out.Flush()
    }
}

function Format-SetupCommandLine {
    param([string] $Exe, [string[]] $ArgumentList)
    $quoted = @($ArgumentList | ForEach-Object {
            if ($_ -match '\s') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
        })
    return "`"$Exe`" $($quoted -join ' ')"
}

function Get-SetupLegDescendantPids {
    param([int] $RootPid)
    $job = Start-Job -ArgumentList $RootPid -ScriptBlock {
        param($root)
        $children = @{}
        foreach ($p in Get-CimInstance Win32_Process -ErrorAction SilentlyContinue) {
            $pp = [int]$p.ParentProcessId
            if (-not $children.ContainsKey($pp)) { $children[$pp] = [System.Collections.Generic.List[int]]::new() }
            $children[$pp].Add([int]$p.ProcessId)
        }
        $seen = [System.Collections.Generic.HashSet[int]]::new()
        $queue = [System.Collections.Generic.Queue[int]]::new()
        $queue.Enqueue($root)
        while ($queue.Count -gt 0) {
            $id = $queue.Dequeue()
            if (-not $seen.Add($id)) { continue }
            if ($children.ContainsKey($id)) {
                foreach ($c in $children[$id]) { $queue.Enqueue($c) }
            }
        }
        return @($seen)
    }
    $done = Wait-Job -Job $job -Timeout 30
    if (-not $done) {
        Stop-Job -Job $job -Force -ErrorAction SilentlyContinue
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
        throw "Get-SetupLegDescendantPids: WMI timed out after 30s (root=$RootPid)"
    }
    $pids = @(Receive-Job -Job $job)
    Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    return $pids
}

function Get-CheckpointServerSetupProcesses {
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "CheckpointServerSetup*" })
}

function Write-SetupProcessFailureUiText {
    param(
        [string] $Reason,
        [int[]] $ProcessIds = @()
    )
    Write-Host "setup-leg-failure: $Reason"
    if ($ProcessIds.Count -eq 0) {
        $ProcessIds = @(Get-CheckpointServerSetupProcesses | ForEach-Object { $_.Id })
    }
    foreach ($line in Get-CheckpointSetupUiText -ProcessIds $ProcessIds) {
        Write-Host "setup-leg-ui: $line"
    }
    $artifactDir = Join-Path $PSScriptRoot "..\..\..\artifacts\installer\acceptance-logs"
    New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
    $report = Join-Path $artifactDir "setup-leg-ui-$(Get-Date -Format 'yyyyMMddHHmmss').utf8.txt"
    $lines = @($Reason) + (Get-CheckpointSetupUiText -ProcessIds $ProcessIds)
    [System.IO.File]::WriteAllLines($report, $lines, [System.Text.UTF8Encoding]::new($false))
    Write-Host "setup-leg-failure: wrote $report"
    [Console]::Out.Flush()
}

function Assert-NoCheckpointSetupProcesses {
    param([string] $Context)
    $procs = Get-CheckpointServerSetupProcesses
    if ($procs.Count -eq 0) { return }
    Write-SetupProcessFailureUiText -Reason "leftover CheckpointServerSetup* before $Context" -ProcessIds @($procs | ForEach-Object { $_.Id })
    throw "setup leg pin: $($procs.Count) leftover CheckpointServerSetup* process(es) at $Context"
}

function Assert-InnoLogClosedForLeg {
    param(
        [string] $LogPath,
        [DateTime] $LegStartUtc
    )
    if (-not (Test-Path -LiteralPath $LogPath)) {
        throw "Inno log missing for leg: $LogPath"
    }
    $tail = @(Write-AcceptanceHarnessTrace "Inno log tail Log closed $LogPath" {
            Get-Content -LiteralPath $LogPath -Tail 40 -Encoding UTF8 -ErrorAction Stop
        })
    if (-not ($tail | Where-Object { $_ -match 'Log closed\.' })) {
        throw "Inno log missing 'Log closed.' for leg: $LogPath"
    }
    $written = (Get-Item -LiteralPath $LogPath).LastWriteTimeUtc
    if ($written -lt $LegStartUtc.AddSeconds(-5)) {
        throw "Inno log not updated for this leg (mtime=$written start=$LegStartUtc): $LogPath"
    }
}

function Stop-SetupLegLeftoversAfterEvidence {
    foreach ($p in Get-CheckpointServerSetupProcesses) {
        Write-Host "Stop-SetupLegLeftoversAfterEvidence: pid=$($p.Id) name=$($p.ProcessName)"
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
}

function Write-InnoSetupHangDiagnostics {
    param([string] $LogPath)
    Write-Host "installer hang diagnostics: === processes (setup/server/pg) ==="
    $patterns = @("CheckpointServerSetup*", "GameClub.Server", "postgres", "CheckpointPostgres", "is*", "powershell")
    foreach ($pat in $patterns) {
        Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like $pat } |
            ForEach-Object {
                Write-Host "installer hang diagnostics: proc pid=$($_.Id) name=$($_.ProcessName) start=$($_.StartTime)"
            }
    }
    foreach ($svc in @("CheckpointServer", "CheckpointPostgres")) {
        & sc.exe query $svc 2>&1 | ForEach-Object { Write-Host "installer hang diagnostics: sc $svc $_" }
    }
    if ($LogPath -and (Test-Path -LiteralPath $LogPath)) {
        Write-Host "installer hang diagnostics: === tail $LogPath ==="
        Get-Content -LiteralPath $LogPath -Tail 120 -ErrorAction SilentlyContinue |
            ForEach-Object { Write-Host "installer hang diagnostics: $_" }
    } else {
        Write-Host "installer hang diagnostics: Inno log missing: $LogPath"
    }
}

function Stop-CheckpointInnoSetupProcesses {
    foreach ($p in @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "CheckpointServerSetup*" })) {
        Write-Host "Stop-CheckpointInnoSetupProcesses: pid=$($p.Id) name=$($p.ProcessName)"
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
}

function Get-InstallerServerAppFingerprint {
    param(
        [string] $ServerExe = "C:\Program Files\Checkpoint\Server\GameClub.Server.exe"
    )
    if (-not (Test-Path -LiteralPath $ServerExe)) { throw "server exe missing: $ServerExe" }
    return (Get-FileHash -LiteralPath $ServerExe -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-DowngradeBlockedLog {
    param([string] $LogPath)
    if (-not (Test-Path -LiteralPath $LogPath)) {
        throw "downgrade blocked: Inno log missing: $LogPath"
    }
    $hit = @(Select-String -LiteralPath $LogPath -Pattern "CHECKPOINT_DOWNGRADE_BLOCKED" -SimpleMatch)
    if ($hit.Count -lt 1) {
        throw "downgrade blocked: log missing CHECKPOINT_DOWNGRADE_BLOCKED line"
    }
}

function Invoke-CheckpointDowngradeBlockedProbe {
    param(
        [string] $LegLabel,
        [string] $OlderSetupExe,
        [string] $PgBinFingerprintBefore,
        [string] $AppFingerprintBefore
    )
    Assert-NoCheckpointSetupProcesses -Context "downgrade probe preflight"

    $logPath = "C:\ProgramData\Checkpoint\logs\acceptance-setup-$LegLabel.log"
    if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath -Force }
    $setupArgs = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=`"$logPath`"")
    $cmdLine = Format-SetupCommandLine -Exe $OlderSetupExe -ArgumentList $setupArgs
    Write-Host "Invoke-CheckpointDowngradeBlockedProbe: leg=$LegLabel cmd=$cmdLine"
    [Console]::Out.Flush()

    $result = Write-AcceptanceHarnessTrace "downgrade blocked setup leg=$LegLabel" {
        Start-CheckpointServerSetup -LegLabel $LegLabel -Exe $OlderSetupExe -ArgumentList $setupArgs `
            -TimeoutSeconds 300 -LogPath $logPath -ExpectExitCode $script:CheckpointSetupExitDowngradeBlocked
    }
    if ($result.ExitCode -ne $script:CheckpointSetupExitDowngradeBlocked) {
        throw "downgrade blocked: expected exit $($script:CheckpointSetupExitDowngradeBlocked); got $($result.ExitCode)"
    }
    Write-AcceptanceHarnessTrace "Assert-DowngradeBlockedLog" { Assert-DowngradeBlockedLog -LogPath $logPath } | Out-Null

    $ui = Get-CheckpointSetupUiText
    if ($ui.Count -gt 0) {
        Write-SetupProcessFailureUiText -Reason "downgrade blocked left visible setup UI" -ProcessIds @()
        throw "downgrade blocked: setup window text present: $($ui -join ' | ')"
    }
    Assert-NoCheckpointSetupProcesses -Context "after downgrade probe"

    Write-AcceptanceHarnessTrace "Assert-InstallerServerServicesRunning" { Assert-InstallerServerServicesRunning } | Out-Null
    Write-AcceptanceHarnessTrace "Assert-InstallerServerHealthOk" { Assert-InstallerServerHealthOk } | Out-Null

    $pgAfter = Get-InstallerPostgreSqlBinFingerprint
    $appAfter = Get-InstallerServerAppFingerprint
    if ($pgAfter -ne $PgBinFingerprintBefore) { throw "downgrade blocked: PostgreSQL\bin fingerprint changed" }
    if ($appAfter -ne $AppFingerprintBefore) { throw "downgrade blocked: server app fingerprint changed" }
}

function Start-CheckpointServerSetup {
    param(
        [string] $LegLabel,
        [string] $Exe,
        [string[]] $ArgumentList,
        [int] $TimeoutSeconds = 2700,
        [string] $LogPath = $null,
        [int] $ExpectExitCode = 0
    )
    $cmdLine = Format-SetupCommandLine -Exe $Exe -ArgumentList $ArgumentList
    Write-Host "Start-CheckpointServerSetup: leg=$LegLabel cmd=$cmdLine"
    [Console]::Out.Flush()

    Assert-NoCheckpointSetupProcesses -Context "start leg $LegLabel"

    $legStartUtc = [DateTime]::UtcNow
  $proc = Write-AcceptanceHarnessTrace "Start-Process leg=$LegLabel" {
        Start-Process -FilePath $Exe -ArgumentList $ArgumentList -PassThru
    }
    $leaderId = $proc.Id
    Write-Host "Start-CheckpointServerSetup: leg=$LegLabel leader=$leaderId"
    [Console]::Out.Flush()

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastLog = [DateTime]::MinValue
    while ([DateTime]::UtcNow -lt $deadline) {
        $legPids = @(Write-AcceptanceHarnessTrace "Get-SetupLegDescendantPids root=$leaderId" {
                Get-SetupLegDescendantPids -RootPid $leaderId
            })
        $aliveLeg = @($legPids | ForEach-Object {
                Get-Process -Id $_ -ErrorAction SilentlyContinue
            } | Where-Object { $null -ne $_ })

        $foreign = @(Get-CheckpointServerSetupProcesses | Where-Object { $legPids -notcontains $_.Id })
        if ($foreign.Count -gt 0) {
            $bootstrapTmpOnly = (
                ($foreign | Where-Object { $_.ProcessName -like 'CheckpointServerSetup*.tmp' }).Count -eq $foreign.Count
            )
            if ($bootstrapTmpOnly -and -not $leaderDone) {
                foreach ($tmp in $foreign) {
                    if ($legPids -notcontains $tmp.Id) { $legPids += $tmp.Id }
                }
                $foreign = @()
            }
        }
        if ($foreign.Count -gt 0) {
            Write-SetupProcessFailureUiText -Reason "foreign CheckpointServerSetup* during leg $LegLabel" -ProcessIds @($foreign | ForEach-Object { $_.Id })
            throw "setup leg pin: foreign setup PID(s) during $LegLabel : $(($foreign | ForEach-Object { $_.ProcessName + ':' + $_.Id }) -join ', ')"
        }

        $leaderDone = $false
        try {
            $leaderDone = [bool](Write-AcceptanceHarnessTrace "WaitForExit leader pid=$leaderId" {
                    $proc.WaitForExit(1000)
                })
        } catch {
            $leaderDone = $true
        }

        if ($leaderDone -and $aliveLeg.Count -eq 0) {
            $exitCode = Write-AcceptanceHarnessTrace "read leader ExitCode pid=$leaderId" {
                    $proc.ExitCode
                }
            if ($ExpectExitCode -ne 0) {
                if ($exitCode -ne $ExpectExitCode) {
                    Write-SetupProcessFailureUiText -Reason "unexpected exit $exitCode expected $ExpectExitCode leg $LegLabel"
                    throw "setup leg pin: exit $exitCode expected $ExpectExitCode (leg=$LegLabel)"
                }
                Assert-NoCheckpointSetupProcesses -Context "expected-exit leg $LegLabel"
                return [PSCustomObject]@{ Process = $proc; ExitCode = $exitCode; LegLabel = $LegLabel }
            }
            Write-AcceptanceHarnessTrace "Assert-InnoLogClosedForLeg $LogPath" {
                Assert-InnoLogClosedForLeg -LogPath $LogPath -LegStartUtc $legStartUtc
            } | Out-Null
            Assert-NoCheckpointSetupProcesses -Context "complete leg $LegLabel"
            return [PSCustomObject]@{ Process = $proc; ExitCode = $exitCode; LegLabel = $LegLabel }
        }

        if ($leaderDone -and $aliveLeg.Count -gt 0) {
            $ui = Get-CheckpointSetupUiText -ProcessIds @($aliveLeg | ForEach-Object { $_.Id })
            if ($ui.Count -gt 0) {
                Write-SetupProcessFailureUiText -Reason "visible UI on leg $LegLabel descendants after leader exit" -ProcessIds @($aliveLeg | ForEach-Object { $_.Id })
                throw "setup leg pin: visible setup UI after leader exit ($LegLabel)"
            }
        }

        if ((([DateTime]::UtcNow - $lastLog).TotalSeconds) -ge 120) {
            $remain = [Math]::Max(0, [int]($deadline - [DateTime]::UtcNow).TotalSeconds)
            $names = if ($aliveLeg.Count -gt 0) {
                ($aliveLeg | ForEach-Object { "$($_.ProcessName):$($_.Id)" }) -join ", "
            } else {
                "(none)"
            }
            Write-Host "Start-CheckpointServerSetup: leg=$LegLabel leader=$leaderId leaderDone=$leaderDone remain=${remain}s aliveLeg=$names"
            [Console]::Out.Flush()
            $lastLog = [DateTime]::UtcNow
        }
        $null = Write-AcceptanceHarnessTrace "Start-Sleep 4" { Start-Sleep -Seconds 4 }
    }
    Write-InnoSetupHangDiagnostics -LogPath $LogPath
    Write-InstallerAcceptanceFailureDiagnostics
    Write-SetupProcessFailureUiText -Reason "timeout leg $LegLabel" -ProcessIds @($legPids)
    Stop-SetupLegLeftoversAfterEvidence
    if (-not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
    throw "Checkpoint server Inno setup did not finish within ${TimeoutSeconds}s (leg=$LegLabel exe=$Exe)"
}

function Install-ServerSetup {
    param(
        [string] $LegLabel,
        [string] $Exe,
        [string] $Accounts,
        [string] $FromVer,
        [string] $DefaultPinsFile = (Join-Path $PSScriptRoot "..\..\ci\default-pins-silent-empty.json")
    )
    if ($FromVer) { $env:CHECKPOINT_UPGRADE_FROM = $FromVer } else { Remove-Item Env:CHECKPOINT_UPGRADE_FROM -ErrorAction SilentlyContinue }

    $logDir = "C:\ProgramData\Checkpoint\logs"
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $logPath = Join-Path $logDir "acceptance-setup-$LegLabel.log"
    if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath -Force }

    $setupArgs = @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/ACCOUNTS=$Accounts", "/SKIPSESSIONCHECK=1",
        "/LOG=`"$logPath`""
    )
    if ($FromVer) {
        if (-not (Test-Path -LiteralPath $DefaultPinsFile)) { throw "DefaultPinsFile missing: $DefaultPinsFile" }
        $setupArgs += "/DEFAULTPINS=$DefaultPinsFile"
    }

    $result = Write-AcceptanceHarnessTrace "Start-CheckpointServerSetup leg=$LegLabel" {
        Start-CheckpointServerSetup -LegLabel $LegLabel -Exe $Exe -ArgumentList $setupArgs -TimeoutSeconds 2700 -LogPath $logPath
    }
    if ($result.ExitCode -ne 0) {
        Write-InnoSetupHangDiagnostics -LogPath $logPath
        Write-SetupProcessFailureUiText -Reason "nonzero exit $($result.ExitCode) leg $LegLabel"
        $artifactDir = Join-Path $PSScriptRoot "..\..\..\artifacts\installer\acceptance-logs"
        New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
        Copy-Item -LiteralPath $logPath -Destination (Join-Path $artifactDir (Split-Path $logPath -Leaf)) -Force
        throw "setup exit $($result.ExitCode) leg=$LegLabel exe=$Exe"
    }
    Write-AcceptanceHarnessTrace "Assert-InnoSetupLogNoFailedRuns $logPath" {
        Assert-InnoSetupLogNoFailedRuns -LogPath $logPath
    } | Out-Null
    Write-AcceptanceHarnessTrace "Assert-InstallerServerServicesRunning" {
        Assert-InstallerServerServicesRunning
    } | Out-Null
    Write-AcceptanceHarnessTrace "Wait-InstallerServerApiPort" {
        Wait-InstallerServerApiPort
    } | Out-Null
}

function Assert-InnoSetupLogNoFailedRuns {
    param([string] $LogPath)
    if (-not (Test-Path -LiteralPath $LogPath)) { return }
    $lines = @(Get-Content -LiteralPath $LogPath -Tail 500 -Encoding UTF8 -ErrorAction Stop)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch 'Process exit code: ([1-9]\d*)') { continue }
        $phase = ($lines[[Math]::Max(0, $i - 6)..$i] | Where-Object { $_ -match 'InstallPhase' } | Select-Object -Last 1)
        if (-not $phase) { $phase = "(unknown phase)" }
        Write-InstallerAcceptanceFailureDiagnostics
        throw "Inno [Run] failed: $phase :: $($lines[$i])"
    }
}

function Assert-KioskPostinstallCompleted {
    param([string] $InnoLogPath)
    $kioskUser = "CheckpointKiosk"
    $common = Join-Path $PSScriptRoot "..\CheckpointInstall.Common.psm1"
    Import-Module $common -Force
    $localUser = Get-LocalUser -Name $kioskUser -ErrorAction SilentlyContinue
    $userOk = $null -ne $localUser
    $passwordLocked = Test-CheckpointKioskPasswordLocked -AccountName $kioskUser
    $envUser = [Environment]::GetEnvironmentVariable("CHECKPOINT_KIOSK_USER", "Machine")
    $envOk = [string]::Equals($envUser, $kioskUser, [StringComparison]::OrdinalIgnoreCase)
    $guardOk = $null -ne (Get-Service CheckpointGuard -ErrorAction SilentlyContinue)
    $logRun = $false
    if (Test-Path -LiteralPath $InnoLogPath) {
        $raw = Get-Content -LiteralPath $InnoLogPath -Raw -Encoding UTF8 -ErrorAction Stop
        if ($raw -match '(?m)Parameters:\s+.*kiosk-postinstall\.ps1') {
            $logRun = $true
        }
    }
    if (-not $logRun -or -not $userOk -or -not $passwordLocked -or -not $envOk -or -not $guardOk) {
        throw "B-5: kiosk-postinstall did not complete (innoRun=$logRun user=$userOk passwordLocked=$passwordLocked kioskUserEnv=$envOk guardSvc=$guardOk)"
    }
}

function Get-InstallerServerJson {
    $path = "C:\ProgramData\Checkpoint\config\server.json"
    if (-not (Test-Path -LiteralPath $path)) { throw "server.json missing" }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Get-InstallerLiveServerBaseUrl {
    $json = Get-InstallerServerJson
    $urls = [string]$json.Urls
    if ([string]::IsNullOrWhiteSpace($urls)) {
        return "http://127.0.0.1:5000"
    }
    foreach ($part in ($urls.Split(';', [StringSplitOptions]::RemoveEmptyEntries))) {
        $token = $part.Trim()
        if (-not ($token -match '^https?://')) { continue }
        $uri = [Uri]$token
        $hostName = $uri.Host
        if ($hostName -eq '0.0.0.0' -or $hostName -eq '+' -or $hostName -eq '*') {
            $hostName = '127.0.0.1'
        }
        $port = if ($uri.Port -gt 0) { $uri.Port } else { 5000 }
        return "$($uri.Scheme)://${hostName}:$port"
    }
    return "http://127.0.0.1:5000"
}

function Set-InstallerKioskWpfLiveProcessEnvironment {
    param([string] $AccountsFile)
    $json = Get-InstallerServerJson
    $cs = [string]$json.ConnectionStrings.Default
    if ([string]::IsNullOrWhiteSpace($cs)) { throw "server.json ConnectionStrings.Default missing" }
    $env:CHECKPOINT_SERVER_URL = Get-InstallerLiveServerBaseUrl
    $env:GAMECLUB_DATABASE = $cs
    $env:GAMECLUB_TEST_DATABASE = $cs
    $env:GAMECLUB_API_KEY = [string]$json.GAMECLUB_API_KEY
    if ($json.OperatorSessionSecret) {
        $env:OperatorSessionSecret = [string]$json.OperatorSessionSecret
    }
    if ($AccountsFile -and (Test-Path -LiteralPath $AccountsFile)) {
        $acc = Get-Content -LiteralPath $AccountsFile -Raw | ConvertFrom-Json
        $env:CHECKPOINT_OPERATOR_USERNAME = [string]$acc.manager1User
        $env:CHECKPOINT_OPERATOR_PIN = [string]$acc.manager1Pin
    }
}

function New-InstallerB6PrefixedTestDatabase {
    $json = Get-InstallerServerJson
    $cs = [string]$json.ConnectionStrings.Default
    $m = [regex]::Match($cs, 'Password=([^;]+)')
    if (-not $m.Success) { throw "server.json connection string missing Password=" }
    $pw = $m.Groups[1].Value
    $dbName = "gameclub_test_b6" + ([guid]::NewGuid().ToString("N").Substring(0, 12))
    if ($dbName -notmatch '^gameclub_test_') { throw "B-6 test db name guard" }
    $psql = Get-InstallerPsqlExecutable
    $env:PGPASSWORD = $pw
    $oldEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $psql -h 127.0.0.1 -p 5433 -U postgres -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE `"$dbName`";" 2>&1
    } finally {
        $ErrorActionPreference = $oldEap
    }
    if ($LASTEXITCODE -ne 0) { throw "B-6: create test database failed: $out" }
    return [PSCustomObject]@{
        Name             = $dbName
        ConnectionString = "Host=127.0.0.1;Port=5433;Database=$dbName;Username=postgres;Password=$pw;SSL Mode=Disable"
    }
}

function Remove-InstallerB6PrefixedTestDatabase {
    param([string] $DatabaseName)
    if ([string]::IsNullOrWhiteSpace($DatabaseName)) { return }
    $json = Get-InstallerServerJson
    $cs = [string]$json.ConnectionStrings.Default
    $m = [regex]::Match($cs, 'Password=([^;]+)')
    if (-not $m.Success) { return }
    $pw = $m.Groups[1].Value
    $psql = Get-InstallerPsqlExecutable
    $env:PGPASSWORD = $pw
    $null = & $psql -h 127.0.0.1 -p 5433 -U postgres -d postgres -c "DROP DATABASE IF EXISTS `"$DatabaseName`" WITH (FORCE);" 2>&1
}

function Invoke-WithInstallerKioskServerVerifyTestEnvironment {
    param([scriptblock] $ScriptBlock)
    $keys = @("GAMECLUB_TEST_DATABASE", "GAMECLUB_DATABASE")
    $saved = @{}
    foreach ($key in $keys) {
        $item = Get-Item -Path "Env:$key" -ErrorAction SilentlyContinue
        $saved[$key] = if ($item) { @{ Present = $true; Value = $item.Value } } else { @{ Present = $false } }
    }
    $testDb = $null
    try {
        Write-AcceptanceHarnessTrace "Invoke-WithInstallerKioskServerVerifyTestEnvironment" {
            $testDb = New-InstallerB6PrefixedTestDatabase
            $env:GAMECLUB_TEST_DATABASE = $testDb.ConnectionString
            Remove-Item Env:GAMECLUB_DATABASE -ErrorAction SilentlyContinue
        } | Out-Null
        & $ScriptBlock
    } finally {
        if ($testDb) {
            Remove-InstallerB6PrefixedTestDatabase -DatabaseName $testDb.Name
        }
        foreach ($key in $keys) {
            if ($saved[$key].Present) {
                Set-Item -Path "Env:$key" -Value $saved[$key].Value
            } else {
                Remove-Item -Path "Env:$key" -ErrorAction SilentlyContinue
            }
        }
    }
}

function Invoke-WithInstallerKioskWpfLiveEnvironment {
    param(
        [string] $AccountsFile,
        [scriptblock] $ScriptBlock
    )
    $keys = @(
        "CHECKPOINT_SERVER_URL", "GAMECLUB_DATABASE", "GAMECLUB_TEST_DATABASE",
        "GAMECLUB_API_KEY", "OperatorSessionSecret",
        "CHECKPOINT_OPERATOR_USERNAME", "CHECKPOINT_OPERATOR_PIN"
    )
    $saved = @{}
    foreach ($key in $keys) {
        $item = Get-Item -Path "Env:$key" -ErrorAction SilentlyContinue
        $saved[$key] = if ($item) { @{ Present = $true; Value = $item.Value } } else { @{ Present = $false } }
    }
    try {
        Assert-InstallerServerServicesRunning
        Write-AcceptanceHarnessTrace "Invoke-WithInstallerKioskWpfLiveEnvironment" {
            Set-InstallerKioskWpfLiveProcessEnvironment -AccountsFile $AccountsFile
        } | Out-Null
        & $ScriptBlock
        if ($LASTEXITCODE -ne 0) { throw "installer live env script exit $LASTEXITCODE" }
    } finally {
        foreach ($key in $keys) {
            if ($saved[$key].Present) {
                Set-Item -Path "Env:$key" -Value $saved[$key].Value
            } else {
                Remove-Item -Path "Env:$key" -ErrorAction SilentlyContinue
            }
        }
    }
}

function Get-InstallerApiHeaders {
    $json = Get-InstallerServerJson
    $key = [string]$json.GAMECLUB_API_KEY
    return @{ "X-Api-Key" = $key }
}

function Write-InstallerAcceptanceFailureDiagnostics {
    foreach ($svcName in @("CheckpointPostgres", "CheckpointServer")) {
        Write-Host "installer acceptance diagnostics: === $svcName ==="
        $s = Get-Service $svcName -ErrorAction SilentlyContinue
        if ($s) {
            Write-Host "installer acceptance diagnostics: Status=$($s.Status) StartType=$($s.StartType)"
            & sc.exe query $svcName 2>&1 | ForEach-Object { Write-Host "installer acceptance diagnostics: $_" }
        } else {
            Write-Host "installer acceptance diagnostics: service not registered"
        }
        try {
            Get-WinEvent -FilterHashtable @{
                LogName   = 'System'
                ProviderName = 'Service Control Manager'
                StartTime = (Get-Date).AddHours(-4)
            } -MaxEvents 100 -ErrorAction Stop |
                Where-Object { $_.Message -match [regex]::Escape($svcName) } |
                Select-Object -First 15 |
                ForEach-Object { Write-Host "installer acceptance diagnostics: $($_.TimeCreated) $($_.Message)" }
        } catch {
            Write-Host "installer acceptance diagnostics: event log read failed: $_"
        }
        if ($svcName -eq "CheckpointServer") {
            try {
                Get-WinEvent -FilterHashtable @{
                    LogName   = 'Application'
                    StartTime = (Get-Date).AddHours(-4)
                } -MaxEvents 80 -ErrorAction Stop |
                    Where-Object {
                        $_.Message -match 'GameClub\.Server|Checkpoint|\.NET Runtime|Microsoft\.AspNetCore'
                    } |
                    Select-Object -First 15 |
                    ForEach-Object { Write-Host "installer acceptance diagnostics: $($_.TimeCreated) $($_.Message)" }
            } catch {
                Write-Host "installer acceptance diagnostics: application event log read failed: $_"
            }
        }
    }
    $logDir = "C:\ProgramData\Checkpoint\logs"
    $pgInitLog = Join-Path $logDir "postgres-init.log"
    if (Test-Path -LiteralPath $pgInitLog) {
        Write-Host "installer acceptance diagnostics: === tail $pgInitLog ==="
        Get-Content -LiteralPath $pgInitLog -Tail 40 -ErrorAction SilentlyContinue |
            ForEach-Object { Write-Host "installer acceptance diagnostics: $_" }
    }
    if (Test-Path -LiteralPath $logDir) {
        Get-ChildItem -LiteralPath $logDir -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 5 |
            ForEach-Object {
                Write-Host "installer acceptance diagnostics: === tail $($_.FullName) ==="
                Get-Content -LiteralPath $_.FullName -Tail 80 -ErrorAction SilentlyContinue |
                    ForEach-Object { Write-Host "installer acceptance diagnostics: $_" }
            }
    } else {
        Write-Host "installer acceptance diagnostics: log dir missing: $logDir"
    }
    $repoRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
    $artifactLogRoot = Join-Path $repoRoot "artifacts/installer/acceptance-logs"
    New-Item -ItemType Directory -Force -Path $artifactLogRoot | Out-Null
    foreach ($b5Path in @(
            (Join-Path $artifactLogRoot "b5-kiosk-install.log"),
            (Join-Path $logDir "b5-kiosk-install.log")
        )) {
        if (-not (Test-Path -LiteralPath $b5Path)) { continue }
        Write-Host "installer acceptance diagnostics: === tail b5 $b5Path ==="
        Get-Content -LiteralPath $b5Path -Tail 120 -ErrorAction SilentlyContinue |
            ForEach-Object { Write-Host "installer acceptance diagnostics: $_" }
        Copy-Item -LiteralPath $b5Path -Destination (Join-Path $artifactLogRoot "b5-kiosk-install.log") -Force -ErrorAction SilentlyContinue
        break
    }
}

function Assert-InstallerServerServicesRunning {
    foreach ($svc in @("CheckpointPostgres", "CheckpointServer")) {
        $s = Get-Service $svc -ErrorAction SilentlyContinue
        if (-not $s) {
            Write-InstallerAcceptanceFailureDiagnostics
            throw "installer acceptance: $svc not registered"
        }
        if ($s.Status -ne "Running") {
            Write-InstallerAcceptanceFailureDiagnostics
            throw "installer acceptance: $svc not Running (Status=$($s.Status))"
        }
    }
}

function Wait-InstallerServerApiPort {
    param([int] $TimeoutSeconds = 120)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $client = $null
        try {
            $client = New-Object System.Net.Sockets.TcpClient
            $connect = $client.BeginConnect("127.0.0.1", 5000, $null, $null)
            if ($connect.AsyncWaitHandle.WaitOne(1000) -and $client.Connected) {
                $client.EndConnect($connect)
                return
            }
        } catch {
            # wait until deadline
        } finally {
            if ($null -ne $client) { $client.Dispose() }
        }
    }
    Write-InstallerAcceptanceFailureDiagnostics
    throw "installer acceptance: TCP 127.0.0.1:5000 not accepting connections within ${TimeoutSeconds}s"
}

function Start-InstallerCheckpointGuard {
    param([int] $MaxAttempts = 6)
    if (-not (Get-Service CheckpointGuard -ErrorAction SilentlyContinue)) {
        throw "CheckpointGuard service not registered"
    }
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            $svc = Get-Service CheckpointGuard -ErrorAction Stop
            if ($svc.Status -eq "Running") {
                Restart-Service CheckpointGuard -Force -ErrorAction Stop
            } else {
                Start-Service CheckpointGuard -ErrorAction Stop
            }
            $deadline = (Get-Date).AddSeconds(45)
            while ((Get-Date) -lt $deadline) {
                if ((Get-Service CheckpointGuard).Status -eq "Running") {
                    return
                }
                Start-Sleep -Seconds 1
            }
            throw "CheckpointGuard not Running after start attempt $attempt"
        } catch {
            if ($attempt -ge $MaxAttempts) { throw }
            Start-Sleep -Seconds 3
        }
    }
}

function Clear-InstallerDotNetTestProcessEnvironment {
    foreach ($key in @(
            "CHECKPOINT_SERVER_URL", "GAMECLUB_DATABASE", "GAMECLUB_TEST_DATABASE",
            "GAMECLUB_API_KEY", "OperatorSessionSecret",
            "CHECKPOINT_OPERATOR_USERNAME", "CHECKPOINT_OPERATOR_PIN"
        )) {
        Remove-Item -Path "Env:$key" -ErrorAction SilentlyContinue
    }
}

function Get-InstallerOperatorUsernames {
    $json = Get-InstallerServerJson
    $cs = [string]$json.ConnectionStrings.Default
    $m = [regex]::Match($cs, 'Password=([^;]+)')
    if (-not $m.Success) { return @() }
    $env:PGPASSWORD = $m.Groups[1].Value
    $psql = Get-InstallerPsqlExecutable
    $oldEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $psql -h 127.0.0.1 -p 5433 -U postgres -d checkpoint -At -c 'SELECT "Username" FROM operator_users ORDER BY "Username";' 2>&1
    } finally {
        $ErrorActionPreference = $oldEap
    }
    if ($LASTEXITCODE -ne 0) { return @($out) }
    return @($out -split "`n" | Where-Object { $_ })
}

function New-InstallerOperatorClient {
    param([string] $Username, [string] $Pin)
    $Username = [string]$Username
    $Pin = [string]$Pin
    $base = (Get-InstallerLiveServerBaseUrl).TrimEnd('/')
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $body = (@{ Username = $Username; Pin = $Pin } | ConvertTo-Json -Compress)
    $lastErr = $null
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        try {
            $null = Invoke-RestMethod -Method Post -Uri "$base/api/operator/auth/login" `
                -Body $body -ContentType "application/json; charset=utf-8" -WebSession $session
            return $session
        } catch {
            $lastErr = $_
            if ($attempt -lt 4) {
                Start-Sleep -Seconds 2
            }
        }
    }
    $names = Get-InstallerOperatorUsernames
    throw "operator login failed for user '$Username' on $base (db users: $($names -join ', ')): $lastErr"
}

function Invoke-InstallerApiJson {
    param(
        [string] $Method,
        [string] $Uri,
        [object] $Body = $null,
        [Microsoft.PowerShell.Commands.WebRequestSession] $Session = $null
    )
    $headers = Get-InstallerApiHeaders
    $params = @{
        Method      = $Method
        Uri         = $Uri
        Headers     = $headers
        UseBasicParsing = $true
    }
    if ($Session) { $params.WebSession = $Session }
    if ($null -ne $Body) {
        $params.Body = ($Body | ConvertTo-Json -Depth 8)
        $params.ContentType = "application/json"
    }
    return Invoke-WebRequest @params
}

function Invoke-InstallerApiJsonAllowCodes {
    param(
        [string] $Method,
        [string] $Uri,
        [object] $Body = $null,
        [Microsoft.PowerShell.Commands.WebRequestSession] $Session = $null,
        [string[]] $AllowErrorCodes = @()
    )
    try {
        return Invoke-InstallerApiJson -Method $Method -Uri $Uri -Body $Body -Session $Session
    }
    catch {
        $detail = [string]$_.ErrorDetails.Message
        if ($detail -and $AllowErrorCodes.Count -gt 0) {
            foreach ($code in $AllowErrorCodes) {
                if ($detail -match [regex]::Escape($code)) {
                    return $null
                }
            }
        }
        throw
    }
}

function Get-InstallerPsqlExecutable {
    $psql = "C:\Program Files\Checkpoint\PostgreSQL\bin\psql.exe"
    if (-not (Test-Path -LiteralPath $psql)) { throw "psql missing at $psql" }
    return $psql
}

function Get-InstallerDbCounts {
    $json = Get-InstallerServerJson
    $cs = [string]$json.ConnectionStrings.Default
    $m = [regex]::Match($cs, 'Password=([^;]+)')
    $pw = $m.Groups[1].Value
    $env:PGPASSWORD = $pw
    $q = @"
SELECT
 (SELECT count(*) FROM customers),
 (SELECT count(*) FROM sessions),
 (SELECT count(*) FROM invoices),
 (SELECT count(*) FROM wallet_ledger);
"@
    $psql = Get-InstallerPsqlExecutable
    $oldEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $psql -h 127.0.0.1 -p 5433 -U postgres -d checkpoint -At -F '|' -c $q 2>&1
    } finally {
        $ErrorActionPreference = $oldEap
    }
    if ($LASTEXITCODE -ne 0) { throw "psql count failed: $out" }
    $parts = $out.Trim() -split '\|'
    return [PSCustomObject]@{
        Customers    = [int]$parts[0]
        Sessions     = [int]$parts[1]
        Invoices     = [int]$parts[2]
        WalletLedger = [int]$parts[3]
    }
}

function Get-ServerSecretsFingerprint {
    $json = Get-InstallerServerJson
    return [PSCustomObject]@{
        OperatorSessionSecret = [string]$json.OperatorSessionSecret
        ApiKey                = [string]$json.GAMECLUB_API_KEY
        InitialUsersJson      = ($json.InitialUsers | ConvertTo-Json -Compress)
    }
}

function Get-InstallerPostgreSqlBinFingerprint {
    param(
        [string] $BinRoot = "C:\Program Files\Checkpoint\PostgreSQL\bin"
    )
    if (-not (Test-Path -LiteralPath $BinRoot)) { throw "PostgreSQL bin missing: $BinRoot" }
    $lines = New-Object System.Collections.Generic.List[string]
    Get-ChildItem -LiteralPath $BinRoot -File | Sort-Object FullName | ForEach-Object {
        $rel = $_.FullName.Substring($BinRoot.Length).TrimStart('\')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $lines.Add("$rel=$hash")
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace("-", "").ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

# Stable listing hash for pgdata on upgrade: relative paths only (sorted), excluding live
# WAL/postmaster/log churn documented for B-2 upgrade pins:
#   postmaster.pid, postmaster.opts, pg_wal\**, log\**, **\*.log, current_logfiles, base\**
# (heap files under base\ are covered by Get-InstallerDbCounts row-count pins.)
function Test-InstallerPgDataPathExcluded {
    param([string] $RelativePath)
    $r = $RelativePath.Replace('/', '\')
    if ($r -eq 'postmaster.pid' -or $r -eq 'postmaster.opts' -or $r -eq 'current_logfiles') { return $true }
    if ($r.StartsWith('pg_wal\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    if ($r.StartsWith('log\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    if ($r.StartsWith('base\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    if ($r.EndsWith('.log', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $false
}

function Get-InstallerPgDataStableListingHash {
    param(
        [string] $PgData = "C:\ProgramData\Checkpoint\pgdata"
    )
    if (-not (Test-Path -LiteralPath $PgData)) { throw "pgdata missing: $PgData" }
    $paths = New-Object System.Collections.Generic.List[string]
    Get-ChildItem -LiteralPath $PgData -File -Recurse | Sort-Object FullName | ForEach-Object {
        $rel = $_.FullName.Substring($PgData.Length).TrimStart('\')
        if (-not (Test-InstallerPgDataPathExcluded -RelativePath $rel)) {
            $paths.Add($rel.Replace('\', '/'))
        }
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($paths -join "`n"))
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace("-", "").ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

function Assert-InstallerServerHealthOk {
    param([int] $TimeoutSeconds = 60)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $resp = Invoke-RestMethod -Uri "http://127.0.0.1:5000/api/kiosk/setup/hello" -Method Get -TimeoutSec 5
            if ($resp.product -eq 'Checkpoint') { return }
        } catch {
            Start-Sleep -Seconds 2
        }
    }
    Write-InstallerAcceptanceFailureDiagnostics
    throw "installer acceptance: server health GET /api/kiosk/setup/hello failed within ${TimeoutSeconds}s"
}