param(
    [string] $ServerSetup,
    [string] $AccountsFile,
    [string] $LogsDir
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null
$installLog = Join-Path $LogsDir "b1-server-install.log"

Write-Host "B-1: silent server install"
$proc = Start-Process -FilePath $ServerSetup -ArgumentList @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=$installLog",
    "/ACCOUNTS=$AccountsFile"
) -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    throw "B-1: server setup exit $($proc.ExitCode)"
}

foreach ($svc in @("CheckpointPostgres", "CheckpointServer")) {
    $s = Get-Service $svc -ErrorAction Stop
    if ($s.Status -ne "Running") {
        throw "B-1: $svc not Running"
    }
}

$config = "C:\ProgramData\Checkpoint\config\server.json"
$raw = Get-Content -LiteralPath $config -Raw
foreach ($pin in @("5678", "8765", "4321", "1234", "1111")) {
    if ($raw -match $pin) {
        throw "B-1: plaintext pin found in server.json"
    }
}
if ($installLog -and (Test-Path -LiteralPath $installLog)) {
    $log = Get-Content -LiteralPath $installLog -Raw
    foreach ($pin in @("5678", "8765", "4321")) {
        if ($log -match $pin) {
            throw "B-1: pin found in install log"
        }
    }
}

# API login checks (401 on defaults)
$base = "http://127.0.0.1:5000"
$acc = Get-Content -LiteralPath $AccountsFile -Raw | ConvertFrom-Json
$loginBody = @{ Username = "manager"; Pin = "1234" } | ConvertTo-Json
try {
    Invoke-RestMethod -Method Post -Uri "$base/api/operator/auth/login" -Body $loginBody -ContentType "application/json" -ErrorAction Stop
    throw "B-1: manager/1234 should be 401"
} catch {
    if ($_.Exception.Response.StatusCode.value__ -ne 401) { throw }
}
Write-Host "B-1: ok"