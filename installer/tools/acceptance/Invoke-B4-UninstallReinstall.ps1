param(
    [string] $ServerSetup,
    [string] $AccountsFile
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")

function Install-ServerSetup {
    param([string] $Exe, [string] $Accounts)
    $p = Start-Process -FilePath $Exe -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/ACCOUNTS=$Accounts") -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "reinstall exit $($p.ExitCode)" }
}

$before = Get-InstallerDbCounts

$unins = Get-ChildItem "C:\Program Files\Checkpoint\Server\unins*.exe" | Select-Object -First 1
if (-not $unins) { throw "B-4: uninstaller missing" }
if (Get-Service CheckpointServer -ErrorAction SilentlyContinue) { } else { throw "B-4: preflight CheckpointServer missing" }
$proc = Start-Process -FilePath $unins.FullName -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES") -Wait -PassThru
if ($proc.ExitCode -ne 0) { throw "B-4: uninstall exit $($proc.ExitCode)" }

if (Get-Service CheckpointServer -ErrorAction SilentlyContinue) { throw "B-4: CheckpointServer still registered after uninstall" }
if (Get-Service CheckpointPostgres -ErrorAction SilentlyContinue) { throw "B-4: CheckpointPostgres still registered after uninstall" }
$fw = Get-NetFirewallRule -DisplayName "Checkpoint Server API" -ErrorAction SilentlyContinue
if ($fw) { throw "B-4: firewall rule still present after uninstall" }
$task = schtasks.exe /Query /TN CheckpointDailyBackup 2>&1
if ($LASTEXITCODE -eq 0) { throw "B-4: CheckpointDailyBackup task still present after uninstall" }

foreach ($p in @("pgdata", "backups", "config")) {
    if (-not (Test-Path "C:\ProgramData\Checkpoint\$p")) { throw "B-4: $p removed" }
}

Install-ServerSetup -Exe $ServerSetup -Accounts $AccountsFile
$after = Get-InstallerDbCounts
if ($before.Customers -ne $after.Customers) { throw "B-4: customer count changed after reinstall" }
$reinstallBackup = Get-ChildItem "C:\ProgramData\Checkpoint\backups\pre-reinstall-kept-*.dump" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $reinstallBackup) { throw "B-4: pre-reinstall-kept backup missing" }
Assert-InstallerServerServicesRunning
Write-Host "B-4: ok backup=$($reinstallBackup.Name)"