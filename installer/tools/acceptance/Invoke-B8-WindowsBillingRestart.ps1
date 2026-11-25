param(
    [string] $AccountsFile
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
Write-Host "B-8 Windows: restart billing parity via live services"
Clear-InstallerDotNetTestProcessEnvironment
Assert-InstallerServerServicesRunning
Wait-InstallerServerApiPort

$acc = Get-Content -LiteralPath $AccountsFile -Raw | ConvertFrom-Json
$opSession = New-InstallerOperatorClient -Username $acc.manager1User -Pin $acc.manager1Pin

Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/stations" -Body @{ id = "PS5-B8"; name = "PS5-B8"; groupId = "ps5" } | Out-Null
Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/stations" -Body @{ id = "PC-B8"; name = "PC-B8"; groupId = "pc-normal" } | Out-Null
$ps5 = Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/sessions" -Body @{ stationId = "PS5-B8"; playerCount = 1 }
$pc = Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/sessions" -Body @{ stationId = "PC-B8"; playerCount = 1 }
$ps5Id = ($ps5.Content | ConvertFrom-Json).id
$pcId = ($pc.Content | ConvertFrom-Json).id

Stop-Service CheckpointServer -Force
Start-Sleep -Seconds 2
Start-Service CheckpointServer -ErrorAction Stop
Start-Sleep -Seconds 5

$ps5Bill = Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/sessions/$ps5Id/stop" -Body @{}
$pcBill = Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/sessions/$pcId/stop" -Body @{}
$ps5Json = $ps5Bill.Content | ConvertFrom-Json
$pcJson = $pcBill.Content | ConvertFrom-Json
if ($null -eq $ps5Json.unroundedPayableToman) { throw "B-8: PS5 bill missing" }
if ($null -eq $pcJson.unroundedPayableToman) { throw "B-8: PC bill missing" }
Write-Host "B-8: ok ps5=$($ps5Json.unroundedPayableToman) pc=$($pcJson.unroundedPayableToman)"