$out = "$env:ProgramData\Checkpoint\evidence\case-12-f1.json"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
$server = $env:CHECKPOINT_SERVER_URL
if ([string]::IsNullOrWhiteSpace($server)) { $server = "http://127.0.0.1:5000" }
$session = $null
$session = New-Object Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions
$cookieJar = New-Object System.Net.CookieContainer
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.CookieContainer = $cookieJar
$handler.UseCookies = $true
$client = New-Object System.Net.Http.HttpClient($handler)
$login = $client.PostAsync(
    "$server/api/operator/auth/login",
    (New-Object System.Net.Http.StringContent('{"username":"operator","pin":"1111"}', [Text.Encoding]::UTF8, "application/json"))).Result
$login.EnsureSuccessStatusCode()
$queue = Invoke-RestMethod -Uri "$server/api/operator/manual-settle-queue" -WebSession ([Microsoft.PowerShell.Commands.WebRequestSession]::new())
$target = @($queue) | Select-Object -First 1
$resolveStatus = $null
$timeGross = $null
if ($target -and $target.sessionId) {
    try {
        $resolve = $client.PostAsync(
            "$server/api/operator/sessions/$($target.sessionId)/manual-settle",
            (New-Object System.Net.Http.StringContent('{"endAt":null}', [Text.Encoding]::UTF8, "application/json"))).Result
        $resolveStatus = [int]$resolve.StatusCode
        if ($resolve.IsSuccessStatusCode) {
            $bill = (Invoke-RestMethod -Uri "$server/api/operator/sessions/$($target.sessionId)/bill" -Headers @{ Cookie = ($handler.CookieContainer.GetCookieHeader([Uri]$server)) })
            $timeGross = $bill.timeGrossToman
        }
    } catch {
        $resolveStatus = $_.Exception.Message
    }
}
@{
    case          = 12
    queueCount    = @($queue).Count
    resolveStatus = $resolveStatus
    timeGross     = $timeGross
    server        = $server
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json -Depth 5 | Set-Content $out
Write-Host "Wrote $out"
