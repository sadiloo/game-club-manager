$ErrorActionPreference = "Stop"

function Write-AcceptanceHarnessTrace {
    param(
        [string] $Label,
        [scriptblock] $Action
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "acceptance-harness: $([DateTime]::UtcNow.ToString('o')) BEGIN $Label"
    try {
        return & $Action
    } finally {
        Write-Host "acceptance-harness: $([DateTime]::UtcNow.ToString('o')) END $Label elapsedMs=$($sw.ElapsedMilliseconds)"
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

function Start-CheckpointServerSetup {
    param(
        [string] $Exe,
        [string[]] $ArgumentList,
        [int] $TimeoutSeconds = 2700,
        [string] $LogPath = $null
    )
    $proc = Start-Process -FilePath $Exe -ArgumentList $ArgumentList -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastLog = [DateTime]::MinValue
    while ([DateTime]::UtcNow -lt $deadline) {
        $children = @(Write-AcceptanceHarnessTrace "Get-Process CheckpointServerSetup*" {
                @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
                        $_.ProcessName -like "CheckpointServerSetup*"
                    })
            })
        $leaderDone = $false
        try {
            $leaderDone = [bool](Write-AcceptanceHarnessTrace "WaitForExit leader pid=$($proc.Id)" {
                    $proc.WaitForExit(1000)
                })
        } catch {
            $leaderDone = $true
        }
        if ($leaderDone -and $children.Count -eq 0) {
            return $proc
        }
        if ((([DateTime]::UtcNow - $lastLog).TotalSeconds) -ge 120) {
            $remain = [Math]::Max(0, [int]($deadline - [DateTime]::UtcNow).TotalSeconds)
            $names = if ($children.Count -gt 0) {
                ($children | ForEach-Object { "$($_.ProcessName):$($_.Id)" }) -join ", "
            } else {
                "(none)"
            }
            Write-Host "Start-CheckpointServerSetup: leader=$($proc.Id) leaderDone=$leaderDone remain=${remain}s children=$names"
            $lastLog = [DateTime]::UtcNow
        }
        $null = Write-AcceptanceHarnessTrace "Start-Sleep 4" { Start-Sleep -Seconds 4 }
    }
    Write-InnoSetupHangDiagnostics -LogPath $LogPath
    Write-InstallerAcceptanceFailureDiagnostics
    Stop-CheckpointInnoSetupProcesses
    if (-not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
    throw "Checkpoint server Inno setup did not finish within ${TimeoutSeconds}s (exe=$Exe)"
}

function Assert-InnoSetupLogNoFailedRuns {
    param([string] $LogPath)
    if (-not (Test-Path -LiteralPath $LogPath)) { return }
    $lines = Get-Content -LiteralPath $LogPath
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch 'Process exit code: ([1-9]\d*)') { continue }
        $phase = ($lines[[Math]::Max(0, $i - 6)..$i] | Where-Object { $_ -match 'InstallPhase' } | Select-Object -Last 1)
        if (-not $phase) { $phase = "(unknown phase)" }
        Write-InstallerAcceptanceFailureDiagnostics
        throw "Inno [Run] postinstall failed: $phase :: $($lines[$i])"
    }
}

function Get-InstallerServerJson {
    $path = "C:\ProgramData\Checkpoint\config\server.json"
    if (-not (Test-Path -LiteralPath $path)) { throw "server.json missing" }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
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

function New-InstallerOperatorClient {
    param([string] $Username, [string] $Pin)
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $body = @{ Username = $Username; Pin = $Pin } | ConvertTo-Json
    $resp = Invoke-WebRequest -Method Post -Uri "http://127.0.0.1:5000/api/operator/auth/login" -Body $body -ContentType "application/json" -WebSession $session -UseBasicParsing
    if ($resp.StatusCode -ne 200) { throw "operator login failed" }
    return $session
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