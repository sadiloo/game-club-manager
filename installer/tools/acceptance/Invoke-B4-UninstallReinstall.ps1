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
$proc = Start-Process -FilePath $unins.FullName -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES") -Wait -PassThru
if ($proc.ExitCode -ne 0) { throw "B-4: uninstall exit $($proc.ExitCode)" }

foreach ($p in @("pgdata", "backups", "config")) {
    if (-not (Test-Path "C:\ProgramData\Checkpoint\$p")) { throw "B-4: $p removed" }
}

Install-ServerSetup -Exe $ServerSetup -Accounts $AccountsFile
$after = Get-InstallerDbCounts
if ($before.Customers -ne $after.Customers) { throw "B-4: customer count changed after reinstall" }
Write-Host "B-4: ok"