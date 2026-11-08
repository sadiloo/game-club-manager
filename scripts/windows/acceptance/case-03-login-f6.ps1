$ErrorActionPreference = "Stop"
$out = "$env:ProgramData\Checkpoint\evidence\case-03-f6.json"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
$server = ($env:CHECKPOINT_SERVER_URL).TrimEnd('/')
if ([string]::IsNullOrWhiteSpace($server)) { $server = "http://127.0.0.1:5000" }
$loginName = $env:CHECKPOINT_F6_USER
if ([string]::IsNullOrWhiteSpace($loginName)) { $loginName = "f6probe" }
$station = $env:CHECKPOINT_STATION_ID
if ([string]::IsNullOrWhiteSpace($station)) { $station = "PC-1" }
$deviceId = $env:CHECKPOINT_DEVICE_ID
$deviceKey = $env:CHECKPOINT_DEVICE_KEY
$headers = @{}
if ($deviceId -and $deviceKey) {
    $headers["X-Device-Id"] = $deviceId
    $headers["X-Device-Key"] = $deviceKey
}
$codes = @()
$locks = @()
for ($i = 0; $i -lt 5; $i++) {
    try {
        $body = @{ loginName = $loginName; password = "wrong"; stationId = $station } | ConvertTo-Json
        $resp = Invoke-WebRequest -Uri "$server/api/kiosk/auth/login" -Method Post -Body $body -ContentType "application/json" -Headers $headers -UseBasicParsing
        $codes += [int]$resp.StatusCode
        $locks += $false
    } catch {
        $codes += [int]$_.Exception.Response.StatusCode
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        $locks += ($reader.ReadToEnd() -match "account_locked")
    }
}
$locked = ($locks | Where-Object { $_ }).Count -ge 1 -and ($locks[-1])
$opSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$opLogin = Invoke-RestMethod -Uri "$server/api/operator/auth/login" -Method Post -Body (@{ username = "operator"; pin = "1111" } | ConvertTo-Json) -ContentType "application/json" -WebSession $opSession
$search = Invoke-RestMethod -Uri "$server/api/operator/customers/search?q=$loginName" -WebSession $opSession
$cust = @($search) | Where-Object { $_.loginName -eq $loginName } | Select-Object -First 1
if (-not $cust) { $cust = @($search) | Select-Object -First 1 }
if (-not $cust) { throw "F6 customer not found: $loginName" }
$customerId = $cust.id
$reset = Invoke-WebRequest -Uri "$server/api/operator/customers/$customerId/credential" -Method Put -Body (@{ loginName = $loginName; password = "pass" } | ConvertTo-Json) -ContentType "application/json" -WebSession $opSession -UseBasicParsing
$resetOk = $reset.StatusCode -eq 200 -or $reset.StatusCode -eq 204
$good = Invoke-WebRequest -Uri "$server/api/kiosk/auth/login" -Method Post -Body (@{ loginName = $loginName; password = "pass"; stationId = $station } | ConvertTo-Json) -ContentType "application/json" -Headers $headers -UseBasicParsing
$reloginOk = $good.StatusCode -eq 200
@{
    case          = 3
    pass          = ($locked -and $resetOk -and $reloginOk)
    locked        = $locked
    resetOk       = $resetOk
    reloginOk     = $reloginOk
    invalidCodes  = $codes
    customerId    = $customerId
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json -Depth 5 | Set-Content $out
if (-not $locked -or -not $resetOk -or -not $reloginOk) { exit 1 }
Write-Host "Wrote $out"
