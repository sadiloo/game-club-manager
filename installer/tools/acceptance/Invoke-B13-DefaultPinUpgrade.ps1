param(
    [string] $ServerSetup,
    [string] $AccountsFile,
    [string] $DefaultPinsFile = (Join-Path $PSScriptRoot "..\..\ci\default-pins-silent.json")
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")

$serverExe = "C:\Program Files\Checkpoint\Server\GameClub.Server.exe"
$acc = Get-Content -LiteralPath $AccountsFile -Raw | ConvertFrom-Json
$operatorUser = [string]$acc.operatorUser

$json = Get-InstallerServerJson
$cs = [string]$json.ConnectionStrings.Default
$m = [regex]::Match($cs, 'Password=([^;]+)')
$env:PGPASSWORD = $m.Groups[1].Value

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $serverExe
$psi.Arguments = "--hash-pin"
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$proc = [System.Diagnostics.Process]::Start($psi)
$proc.StandardInput.Write("1111")
$proc.StandardInput.Close()
$hash1111 = $proc.StandardOutput.ReadToEnd().Trim()
$proc.WaitForExit()
if ($proc.ExitCode -ne 0) { throw "B-13: hash-pin failed" }

& psql -h 127.0.0.1 -p 5433 -U postgres -d checkpoint -c "UPDATE operator_users SET pin_hash='$hash1111' WHERE username='$operatorUser';" | Out-Null

$check = & $serverExe --check-default-pins 2>&1 | Out-String
if ($check -notmatch [regex]::Escape($operatorUser)) {
    throw "B-13: expected $operatorUser in --check-default-pins; got: $check"
}

if (-not (Test-Path -LiteralPath $DefaultPinsFile)) {
    throw "B-13: DefaultPinsFile missing: $DefaultPinsFile"
}

$fromVer = (Get-Item -LiteralPath "C:\Program Files\Checkpoint\Server\GameClub.Server.exe").VersionInfo.ProductVersion
$env:CHECKPOINT_UPGRADE_FROM = $fromVer
$procSetup = Start-Process -FilePath $ServerSetup -ArgumentList @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SKIPSESSIONCHECK=1",
    "/DEFAULTPINS=$DefaultPinsFile"
) -Wait -PassThru
if ($procSetup.ExitCode -ne 0) { throw "B-13: upgrade exit $($procSetup.ExitCode)" }

$checkAfter = & $serverExe --check-default-pins 2>&1 | Out-String
if ($checkAfter.Trim()) {
    throw "B-13: default pins remain: $checkAfter"
}

$replacement = (Get-Content -LiteralPath $DefaultPinsFile -Raw | ConvertFrom-Json)[0].pin
$loginBody = @{ username = $operatorUser; pin = $replacement } | ConvertTo-Json
try {
    Invoke-RestMethod -Uri "http://127.0.0.1:5000/api/operator/login" -Method POST -Body $loginBody -ContentType "application/json" | Out-Null
} catch {
    throw "B-13: login with replacement pin failed: $_"
}

$badBody = @{ username = $operatorUser; pin = "1111" } | ConvertTo-Json
try {
    Invoke-RestMethod -Uri "http://127.0.0.1:5000/api/operator/login" -Method POST -Body $badBody -ContentType "application/json" | Out-Null
    throw "B-13: old pin 1111 must not work"
} catch {
    if ($_.ErrorDetails.Message -notmatch "401" -and $_.Exception.Message -notmatch "401") {
        Write-Host "B-13: old pin rejected"
    }
}

Write-Host "B-13: ok"