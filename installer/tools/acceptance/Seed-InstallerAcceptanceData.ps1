param(
    [string] $AccountsFile,
    [switch] $WithOpenSessions
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
$acc = Get-Content -LiteralPath $AccountsFile -Raw | ConvertFrom-Json
$opSession = New-InstallerOperatorClient -Username $acc.manager1User -Pin $acc.manager1Pin

Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/stations" -Body @{ id = "PS5-B2"; name = "PS5-B2"; groupId = "ps5" } | Out-Null
Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/stations" -Body @{ id = "PC-B2"; name = "PC-B2"; groupId = "pc-normal" } | Out-Null

$customerResp = Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/operator/customers" -Body @{ name = "B2 Customer"; mobile = "09120000000" } -Session $opSession
$customer = ($customerResp.Content | ConvertFrom-Json)
$customerId = $customer.id

Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/operator/members/customers/$customerId/wallet/top-up" -Body @{ amountToman = 500000; method = "Cash" } -Session $opSession | Out-Null

if ($WithOpenSessions) {
    Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/sessions" -Body @{ stationId = "PS5-B2"; playerCount = 1 } | Out-Null
    Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/sessions" -Body @{ stationId = "PC-B2"; playerCount = 1 } | Out-Null
} else {
    $s = Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/sessions" -Body @{ stationId = "PS5-B2"; playerCount = 1 }
    $sid = ($s.Content | ConvertFrom-Json).id
    Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/sessions/$sid/stop" -Body @{} | Out-Null
}

Write-Host "Seed-InstallerAcceptanceData: ok WithOpenSessions=$WithOpenSessions customerId=$customerId"