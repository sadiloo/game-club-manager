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
for ($i = 0; $i -lt 5; $i++) {
    try {
        $body = @{ loginName = $loginName; password = "wrong"; stationId = $station } | ConvertTo-Json
        Invoke-WebRequest -Uri "$server/api/kiosk/auth/login" -Method Post -Body $body -ContentType "application/json" -Headers $headers -UseBasicParsing | Out-Null
        $codes += 0
    } catch {
        $codes += [int]$_.Exception.Response.StatusCode
    }
}
$locked = ($codes | Where-Object { $_ -ge 400 }).Count -ge 5
$op = Invoke-RestMethod -Uri "$server/api/operator/auth/login" -Method Post -Body (@{ username = "operator"; pin = "1111" } | ConvertTo-Json) -ContentType "application/json"
$opSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$opSession.Headers["Authorization"] = "Bearer $($op.token)"
$customers = Invoke-RestMethod -Uri "$server/api/operator/members/customers" -WebSession $opSession
$cust = @($customers) | Where-Object { $_.credentials.loginName -eq $loginName } | Select-Object -First 1
if (-not $cust) { throw "F6 customer not found: $loginName" }
$reset = Invoke-WebRequest -Uri "$server/api/operator/members/customers/$($cust.id)/reset-password" -Method Post -WebSession $opSession -UseBasicParsing
$resetOk = $reset.StatusCode -eq 204
$good = Invoke-WebRequest -Uri "$server/api/kiosk/auth/login" -Method Post -Body (@{ loginName = $loginName; password = "pass"; stationId = $station } | ConvertTo-Json) -ContentType "application/json" -Headers $headers -UseBasicParsing
$reloginOk = $good.StatusCode -eq 200
@{
    case          = 3
    pass          = ($locked -and $resetOk -and $reloginOk)
    locked        = $locked
    resetOk       = $resetOk
    reloginOk     = $reloginOk
    invalidCodes  = $codes
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json -Depth 5 | Set-Content $out
if (-not $locked -or -not $resetOk -or -not $reloginOk) { exit 1 }
Write-Host "Wrote $out"
