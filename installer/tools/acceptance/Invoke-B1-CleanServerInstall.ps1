param(
    [string] $ServerSetup,
    [string] $AccountsFile,
    [string] $LogsDir
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null
$installLog = Join-Path $LogsDir "b1-server-install.log"

Write-Host "B-1: silent server install"
$legLog = "C:\ProgramData\Checkpoint\logs\acceptance-setup-b1-clean.log"
if (Test-Path -LiteralPath $legLog) { Remove-Item -LiteralPath $legLog -Force }
$result = Start-CheckpointServerSetup -LegLabel "b1-clean" -Exe $ServerSetup -ArgumentList @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=`"$legLog`"",
    "/ACCOUNTS=$AccountsFile"
) -LogPath $legLog
if ($result.ExitCode -ne 0) {
    throw "B-1: server setup exit $($result.ExitCode)"
}
Copy-Item -LiteralPath $legLog -Destination $installLog -Force
Assert-InnoSetupLogNoFailedRuns -LogPath $legLog
Assert-InstallerServerServicesRunning
Wait-InstallerServerApiPort
Assert-InstallerServerHealthOk

$config = "C:\ProgramData\Checkpoint\config\server.json"
$raw = Get-Content -LiteralPath $config -Raw
foreach ($pin in @("5678", "8765", "4321", "1234", "1111")) {
    if ($raw -match $pin) {
        throw "B-1: plaintext pin found in server.json"
    }
}
$log = Get-Content -LiteralPath $installLog -Raw
foreach ($pin in @("5678", "8765", "4321")) {
    if ($log -match $pin) {
        throw "B-1: pin found in install log"
    }
}

$base = (Get-InstallerLiveServerBaseUrl).TrimEnd("/")
$acc = Get-Content -LiteralPath $AccountsFile -Raw | ConvertFrom-Json
$jsonCfg = Get-InstallerServerJson
if (@($jsonCfg.InitialUsers).Count -ne 3) {
    throw "B-1: expected 3 InitialUsers; got $(@($jsonCfg.InitialUsers).Count)"
}

function Assert-LoginFails401([string]$User, [string]$Pin) {
    $body = @{ Username = $User; Pin = $Pin } | ConvertTo-Json
    try {
        Invoke-RestMethod -Method Post -Uri "$base/api/operator/auth/login" -Body $body -ContentType "application/json" -ErrorAction Stop
        throw "B-1: $User/$Pin must be 401"
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 401) { throw }
    }
}

Assert-LoginFails401 -User "manager" -Pin "1234"
Assert-LoginFails401 -User "operator" -Pin "1111"
$goodBody = @{ Username = $acc.manager1User; Pin = $acc.manager1Pin } | ConvertTo-Json
$user = Invoke-RestMethod -Method Post -Uri "$base/api/operator/auth/login" -Body $goodBody -ContentType "application/json"
if (-not $user.username) { throw "B-1: manager login failed" }

$headers = Get-InstallerApiHeaders
$ratesResp = Invoke-RestMethod -Method Get -Uri "$base/api/rates" -Headers $headers
$lines = @($ratesResp.lines)
$ps5Line = $lines | Where-Object { $_.groupId -eq "ps5" -and $_.playMode -eq "Offline" -and $_.playerCount -eq 1 -and -not $_.isSpecialBand } | Select-Object -First 1
if (-not $ps5Line) { throw "B-1: ps5 offline 1-player rate line missing" }
if ([int]$ps5Line.hourlyToman -ne 110000) { throw "B-1: ps5 offline 1-player rate expected 110000; got $($ps5Line.hourlyToman)" }

$shellExe = "C:\Program Files\Checkpoint\Server\GameClub.Shell.exe"
if (-not (Test-Path -LiteralPath $shellExe)) {
    Write-Host "B-1: shell exe not in payload (server-only); skip shell ACL pin"
} else {
    $acl = icacls $shellExe 2>&1 | Out-String
    if ($acl -notmatch "Administrators") { throw "B-1: shell exe ACL unexpected" }
}

Write-Host "B-1: ok"