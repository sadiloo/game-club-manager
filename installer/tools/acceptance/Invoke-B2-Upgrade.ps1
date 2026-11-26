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
$billingBefore = Write-AcceptanceHarnessTrace "Get-InstallerBillingFingerprint (before)" { Get-InstallerBillingFingerprint }
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
$billingAfter = Write-AcceptanceHarnessTrace "Get-InstallerBillingFingerprint (after)" { Get-InstallerBillingFingerprint }
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
if ($billingBefore.WalletLedgerRealSumToman -ne $billingAfter.WalletLedgerRealSumToman) {
    throw "B-2: wallet ledger real money sum changed ($($billingBefore.WalletLedgerRealSumToman) -> $($billingAfter.WalletLedgerRealSumToman))"
}
if ($billingBefore.WalletLedgerGiftSumToman -ne $billingAfter.WalletLedgerGiftSumToman) {
    throw "B-2: wallet ledger gift money sum changed ($($billingBefore.WalletLedgerGiftSumToman) -> $($billingAfter.WalletLedgerGiftSumToman))"
}
if ($billingBefore.LeaseKeyCount -ne $billingAfter.LeaseKeyCount) {
    throw "B-2: kiosk lease signing key count changed"
}
if ($billingBefore.DeviceLeaseKeyPrefix -ne $billingAfter.DeviceLeaseKeyPrefix) {
    throw "B-2: kiosk device lease verify key prefix changed"
}
if ($secrets.OperatorSessionSecret -ne $secretsAfter.OperatorSessionSecret) {
    throw "B-2: OperatorSessionSecret changed"
}
if ($secrets.ApiKey -ne $secretsAfter.ApiKey) {
    throw "B-2: API key changed"
}
$beforeUsers = $secrets.InitialUsersJson
$afterUsers = $secretsAfter.InitialUsersJson
if ($beforeUsers -ne $afterUsers) {
    throw "B-2: InitialUsers changed during upgrade"
}

$pre = Get-ChildItem "C:\ProgramData\Checkpoint\backups\pre-upgrade-$LowVersion-*.dump" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $pre) { throw "B-2: pre-upgrade dump missing" }

$upgradeLog = "C:\ProgramData\Checkpoint\logs\upgrade.log"
if (-not (Test-Path $upgradeLog)) { throw "B-2: upgrade.log missing" }
$downtimeLine = Select-String -Path $upgradeLog -Pattern "downtimeSeconds=(\d+)" | Select-Object -Last 1
if (-not $downtimeLine) { throw "B-2: upgrade downtime not logged" }
$downtimeSec = [int]$downtimeLine.Matches[0].Groups[1].Value
if ($downtimeSec -ge 600) { throw "B-2: upgrade downtime $downtimeSec s must be < 600" }

if (-not $SkipDowngradeCheck) {
    $pgPin = Get-InstallerPostgreSqlBinFingerprint
    $appPin = Get-InstallerServerAppFingerprint
    Write-AcceptanceHarnessTrace "B-2 downgrade blocked probe ($LowVersion over $HighVersion)" {
        Invoke-CheckpointDowngradeBlockedProbe -LegLabel "b2-$Mode-downgrade-blocked" -OlderSetupExe $LowSetup `
            -PgBinFingerprintBefore $pgPin -AppFingerprintBefore $appPin
    } | Out-Null
}

Write-Host "B-2 ($Mode): ok"
[Console]::Out.Flush()