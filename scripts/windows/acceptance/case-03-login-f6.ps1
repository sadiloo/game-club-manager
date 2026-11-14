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
$customerId = $env:CHECKPOINT_F6_CUSTOMER_ID
$opSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$null = Invoke-RestMethod -Uri "$server/api/operator/auth/login" -Method Post -Body (@{ username = "operator"; pin = "1111" } | ConvertTo-Json) -ContentType "application/json" -WebSession $opSession
if (-not $customerId) {
    $create = Invoke-RestMethod -Uri "$server/api/operator/customers" -Method Post -Body (@{ name = "F6 Script"; mobile = "09125550103" } | ConvertTo-Json) -ContentType "application/json" -WebSession $opSession
    $customerId = $create.id
    $loginName = "f6script$([Guid]::NewGuid().ToString('N').Substring(0,6))"
    $null = Invoke-RestMethod -Uri "$server/api/operator/customers/$customerId/credential" -Method Put -Body (@{ loginName = $loginName; password = "wrong-seed" } | ConvertTo-Json) -ContentType "application/json" -WebSession $opSession
    $null = Invoke-RestMethod -Uri "$server/api/operator/members/customers/$customerId/wallet/top-up" -Method Post -Body (@{ method = "Cash"; amountToman = 500000 } | ConvertTo-Json) -ContentType "application/json" -WebSession $opSession
}
$codes = @()
$lockedCodes = @()
for ($i = 0; $i -lt 5; $i++) {
    try {
        $body = @{ loginName = $loginName; password = "wrong"; stationId = $station } | ConvertTo-Json
        $resp = Invoke-WebRequest -Uri "$server/api/kiosk/auth/login" -Method Post -Body $body -ContentType "application/json" -Headers $headers -UseBasicParsing
        $codes += [int]$resp.StatusCode
        $lockedCodes += $null
    } catch {
        $codes += [int]$_.Exception.Response.StatusCode
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        $json = $reader.ReadToEnd()
        $lockedCodes += $json
    }
}
$sixthLocked = $false
try {
    $body = @{ loginName = $loginName; password = "wrong"; stationId = $station } | ConvertTo-Json
    Invoke-WebRequest -Uri "$server/api/kiosk/auth/login" -Method Post -Body $body -ContentType "application/json" -Headers $headers -UseBasicParsing | Out-Null
    $codes += 200
} catch {
    $codes += [int]$_.Exception.Response.StatusCode
    $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
    $json = $reader.ReadToEnd()
    $sixthLocked = $json -match "account_locked"
}
$reset = Invoke-WebRequest -Uri "$server/api/operator/customers/$customerId/credential" -Method Put -Body (@{ loginName = $loginName; password = "pass" } | ConvertTo-Json) -ContentType "application/json" -WebSession $opSession -UseBasicParsing
$resetOk = $reset.StatusCode -eq 200 -or $reset.StatusCode -eq 204
$good = Invoke-WebRequest -Uri "$server/api/kiosk/auth/login" -Method Post -Body (@{ loginName = $loginName; password = "pass"; stationId = $station } | ConvertTo-Json) -ContentType "application/json" -Headers $headers -UseBasicParsing
$reloginOk = $good.StatusCode -eq 200
$invalidOnly = ($codes[0..4] | Where-Object { $_ -eq 401 }).Count -eq 5
$locked = $invalidOnly -and $sixthLocked
@{
    case          = 3
    pass          = ($locked -and $resetOk -and $reloginOk)
    locked        = $locked
    lockedOnSixth = $sixthLocked
    resetOk       = $resetOk
    reloginOk     = $reloginOk
    invalidCodes  = $codes
    customerId    = $customerId
    loginName     = $loginName
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json -Depth 5 | Set-Content $out
if (-not $locked -or -not $resetOk -or -not $reloginOk) { exit 1 }
Write-Host "Wrote $out"
