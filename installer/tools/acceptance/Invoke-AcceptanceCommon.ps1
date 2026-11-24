$ErrorActionPreference = "Stop"

function Assert-InnoSetupLogNoFailedRuns {
    param([string] $LogPath)
    if (-not (Test-Path -LiteralPath $LogPath)) { return }
    $failedRun = Select-String -LiteralPath $LogPath -Pattern 'Process exit code: ([1-9]\d*)' | Select-Object -First 1
    if ($failedRun) {
        throw "Inno [Run] postinstall failed ($($failedRun.Line))"
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
    $out = & psql -h 127.0.0.1 -p 5433 -U postgres -d checkpoint -At -F '|' -c $q 2>&1
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