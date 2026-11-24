param(
    [string] $LowSetup,
    [string] $HighSetup,
    [string] $AccountsFile,
    [string] $LowVersion,
    [string] $HighVersion,
    [ValidateSet("WithoutOpenSessions", "WithOpenSessions")]
    [string] $Mode,
    [switch] $SkipDowngradeCheck
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")

$withOpen = $Mode -eq "WithOpenSessions"
Write-Host "B-2 ($Mode): low=$LowVersion high=$HighVersion"
[Console]::Out.Flush()

if (-not (Test-Path "C:\ProgramData\Checkpoint\config\server.json")) {
    Write-AcceptanceHarnessTrace "B-2 leg low install $LowVersion" {
        Install-ServerSetup -LegLabel "b2-$Mode-low-$LowVersion" -Exe $LowSetup -Accounts $AccountsFile -FromVer ""
    } | Out-Null
}

Write-AcceptanceHarnessTrace "Seed-InstallerAcceptanceData" {
    . (Join-Path $PSScriptRoot "Seed-InstallerAcceptanceData.ps1") -AccountsFile $AccountsFile -WithOpenSessions:$withOpen
} | Out-Null

$before = Write-AcceptanceHarnessTrace "Get-InstallerDbCounts (before upgrade)" { Get-InstallerDbCounts }
$secrets = Write-AcceptanceHarnessTrace "Get-ServerSecretsFingerprint (before)" { Get-ServerSecretsFingerprint }

Write-Host "B-2 ($Mode): fingerprint PostgreSQL\bin + pgdata before high upgrade"
[Console]::Out.Flush()
$pgBinBefore = Write-AcceptanceHarnessTrace "Get-InstallerPostgreSqlBinFingerprint (before)" { Get-InstallerPostgreSqlBinFingerprint }
$pgDataListingBefore = Write-AcceptanceHarnessTrace "Get-InstallerPgDataStableListingHash (before)" { Get-InstallerPgDataStableListingHash }

$defaultPinsFile = Join-Path $PSScriptRoot "..\..\ci\default-pins-silent-empty.json"
$pinsRaw = (Get-Content -LiteralPath $defaultPinsFile -Raw).Trim()
if ($pinsRaw -ne "[]") {
    throw "B-2: expected empty DEFAULTPINS ([]); got: $pinsRaw"
}
Write-Host "B-2 ($Mode): DEFAULTPINS empty — upgrade-pins no-op; pin replacement asserted in B-13"

Write-Host "B-2 ($Mode): silent high upgrade install"
[Console]::Out.Flush()
Write-AcceptanceHarnessTrace "B-2 leg high upgrade $LowVersion->$HighVersion" {
    Install-ServerSetup -LegLabel "b2-$Mode-high-$HighVersion" -Exe $HighSetup -Accounts $AccountsFile -FromVer $LowVersion
} | Out-Null

$after = Write-AcceptanceHarnessTrace "Get-InstallerDbCounts (after upgrade)" { Get-InstallerDbCounts }
$secretsAfter = Write-AcceptanceHarnessTrace "Get-ServerSecretsFingerprint (after)" { Get-ServerSecretsFingerprint }
$pgBinAfter = Write-AcceptanceHarnessTrace "Get-InstallerPostgreSqlBinFingerprint (after)" { Get-InstallerPostgreSqlBinFingerprint }
$pgDataListingAfter = Write-AcceptanceHarnessTrace "Get-InstallerPgDataStableListingHash (after)" { Get-InstallerPgDataStableListingHash }

Write-AcceptanceHarnessTrace "Assert-InstallerServerHealthOk" { Assert-InstallerServerHealthOk } | Out-Null

if ($pgBinBefore -ne $pgBinAfter) {
    throw "B-2: PostgreSQL\bin fingerprint changed after upgrade"
}
if ($pgDataListingBefore -ne $pgDataListingAfter) {
    throw "B-2: pgdata stable listing hash changed after upgrade"
}

if ($before.Customers -ne $after.Customers -or $before.Sessions -ne $after.Sessions) {
    throw "B-2: row count mismatch customers/sessions"
}
if ($before.Invoices -ne $after.Invoices -or $before.WalletLedger -ne $after.WalletLedger) {
    throw "B-2: row count mismatch invoices/wallet_ledger"
}
if ($secrets.OperatorSessionSecret -ne $secretsAfter.OperatorSessionSecret) {
    throw "B-2: OperatorSessionSecret changed"
}
if ($secrets.ApiKey -ne $secretsAfter.ApiKey) {
    throw "B-2: API key changed"
}

$pre = Get-ChildItem "C:\ProgramData\Checkpoint\backups\pre-upgrade-$LowVersion-*.dump" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $pre) { throw "B-2: pre-upgrade dump missing" }

$upgradeLog = "C:\ProgramData\Checkpoint\logs\upgrade.log"
if (-not (Test-Path $upgradeLog)) { throw "B-2: upgrade.log missing" }
if (-not (Select-String -Path $upgradeLog -Pattern "downtimeSeconds=")) { throw "B-2: upgrade downtime not logged" }

if (-not $SkipDowngradeCheck) {
    Assert-NoCheckpointSetupProcesses -Context "B-2 downgrade check"
    $downLog = "C:\ProgramData\Checkpoint\logs\acceptance-setup-b2-downgrade-probe.log"
    if (Test-Path -LiteralPath $downLog) { Remove-Item -LiteralPath $downLog -Force }
    $downArgs = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=`"$downLog`"")
    $downCmd = Format-SetupCommandLine -Exe $LowSetup -ArgumentList $downArgs
    Write-Host "B-2 ($Mode): downgrade probe cmd=$downCmd"
    [Console]::Out.Flush()
    $down = Write-AcceptanceHarnessTrace "B-2 downgrade probe Start-Process" {
        Start-Process -FilePath $LowSetup -ArgumentList $downArgs -Wait -PassThru
    }
    if ($down.ExitCode -eq 0) {
        throw "B-2: downgrade must not succeed silently"
    }
    Assert-NoCheckpointSetupProcesses -Context "after B-2 downgrade probe"
}

Write-Host "B-2 ($Mode): ok"
[Console]::Out.Flush()