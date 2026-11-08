$ErrorActionPreference = "Stop"
$out = "$env:ProgramData\Checkpoint\evidence\case-12-f1.json"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
$server = ($env:CHECKPOINT_SERVER_URL).TrimEnd('/')
if ([string]::IsNullOrWhiteSpace($server)) { $server = "http://127.0.0.1:5000" }
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$login = Invoke-RestMethod -Uri "$server/api/operator/auth/login" -Method Post -Body (@{ username = "operator"; pin = "1111" } | ConvertTo-Json) -ContentType "application/json" -WebSession $session
if (-not $login.token) { throw "operator login missing token" }
$session.Headers["Authorization"] = "Bearer $($login.token)"
$queue = Invoke-RestMethod -Uri "$server/api/operator/manual-settle-queue" -WebSession $session
$target = @($queue) | Where-Object { $_.reason -eq "powered_offline_after_autoclose" } | Select-Object -First 1
$resolveStatus = $null
$timeGross = $null
$pass = $false
if ($target -and $target.sessionId) {
    $before = Invoke-RestMethod -Uri "$server/api/operator/sessions/$($target.sessionId)/bill" -WebSession $session
    $resolve = Invoke-WebRequest -Uri "$server/api/operator/sessions/$($target.sessionId)/manual-settle" -Method Post -Body '{"endAt":null}' -ContentType "application/json" -WebSession $session -UseBasicParsing
    $resolveStatus = [int]$resolve.StatusCode
    $after = Invoke-RestMethod -Uri "$server/api/operator/sessions/$($target.sessionId)/bill" -WebSession $session
    $timeGross = $after.timeGrossToman
    $pass = $resolve.IsSuccessStatusCode -and ($before.timeGrossToman -eq $after.timeGrossToman)
}
@{
    case          = 12
    pass          = $pass
    queueCount    = @($queue).Count
    resolveStatus = $resolveStatus
    timeGross     = $timeGross
    server        = $server
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json -Depth 5 | Set-Content $out
if (-not $pass) { exit 1 }
Write-Host "Wrote $out"
