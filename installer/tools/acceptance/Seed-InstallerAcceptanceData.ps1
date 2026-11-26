param(
    [string] $AccountsFile,
    [switch] $WithOpenSessions
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
$acc = Get-Content -LiteralPath $AccountsFile -Raw | ConvertFrom-Json
$opSession = New-InstallerOperatorClient -Username $acc.manager1User -Pin $acc.manager1Pin

$seedMobile = "09120000000"

Invoke-InstallerApiJsonAllowCodes -Method Post -Uri "http://127.0.0.1:5000/api/stations" `
    -Body @{ id = "PS5-B2"; name = "PS5-B2"; groupId = "ps5" } `
    -AllowErrorCodes @("duplicate_station") | Out-Null
Invoke-InstallerApiJsonAllowCodes -Method Post -Uri "http://127.0.0.1:5000/api/stations" `
    -Body @{ id = "PC-B2"; name = "PC-B2"; groupId = "pc-normal" } `
    -AllowErrorCodes @("duplicate_station") | Out-Null

$customerResp = Invoke-InstallerApiJsonAllowCodes -Method Post -Uri "http://127.0.0.1:5000/api/operator/customers" `
    -Body @{ name = "B2 Customer"; mobile = $seedMobile } -Session $opSession `
    -AllowErrorCodes @("duplicate_mobile", "duplicate_membership_code")
if ($customerResp) {
    $customerId = (($customerResp.Content | ConvertFrom-Json).id)
} else {
    $found = Invoke-InstallerApiJson -Method Get -Uri "http://127.0.0.1:5000/api/operator/customers/search?q=$seedMobile" -Session $opSession
    $rows = @($found.Content | ConvertFrom-Json)
    if ($rows.Count -lt 1) { throw "Seed-InstallerAcceptanceData: customer not found for mobile $seedMobile" }
    $customerId = $rows[0].id
}

Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/operator/members/customers/$customerId/wallet/top-up" `
    -Body @{ amountToman = 500000; method = "Cash"; commandId = [guid]::NewGuid() } -Session $opSession | Out-Null
# R10h (rr6): a gift wallet entry too, so B-2's wallet_ledger gift-sum compare is not 0 == 0.
$giftResp = Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/operator/members/customers/$customerId/wallet/gift" `
    -Body @{ giftToman = 70000 } -Session $opSession
$giftWallet = $giftResp.Content | ConvertFrom-Json
Write-Host "Seed-InstallerAcceptanceData: gift 70000 issued (wallet gift=$($giftWallet.giftToman))"

if ($WithOpenSessions) {
    Invoke-InstallerApiJsonAllowCodes -Method Post -Uri "http://127.0.0.1:5000/api/sessions" `
        -Body @{ stationId = "PS5-B2"; playerCount = 1 } -AllowErrorCodes @("station_busy", "session_elsewhere") | Out-Null
    Invoke-InstallerApiJsonAllowCodes -Method Post -Uri "http://127.0.0.1:5000/api/sessions" `
        -Body @{ stationId = "PC-B2"; playerCount = 1 } -AllowErrorCodes @("station_busy", "session_elsewhere") | Out-Null
} else {
    $s = Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/sessions" -Body @{ stationId = "PS5-B2"; playerCount = 1 }
    $sid = ($s.Content | ConvertFrom-Json).id
    Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/sessions/$sid/stop" -Body @{} | Out-Null
}

Write-Host "Seed-InstallerAcceptanceData: ok WithOpenSessions=$WithOpenSessions customerId=$customerId"