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

function Install-ServerSetup {
    param(
        [string] $Exe,
        [string] $Accounts,
        [string] $FromVer,
        [string] $DefaultPinsFile = (Join-Path $PSScriptRoot "..\..\ci\default-pins-silent-empty.json")
    )
    if ($FromVer) { $env:CHECKPOINT_UPGRADE_FROM = $FromVer }
    $logPath = "C:\ProgramData\Checkpoint\logs\acceptance-setup.log"
    New-Item -ItemType Directory -Force -Path (Split-Path $logPath -Parent) | Out-Null
    $args = @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/ACCOUNTS=$Accounts", "/SKIPSESSIONCHECK=1",
        "/LOG=`"$logPath`""
    )
    if ($FromVer) {
        if (-not (Test-Path -LiteralPath $DefaultPinsFile)) { throw "DefaultPinsFile missing: $DefaultPinsFile" }
        $args += "/DEFAULTPINS=$DefaultPinsFile"
    }
    $proc = Start-Process -FilePath $Exe -ArgumentList $args -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $logPath) {
            Write-Host "=== tail $logPath ==="
            Get-Content -LiteralPath $logPath -Tail 80 | ForEach-Object { Write-Host $_ }
            $artifactDir = Join-Path $PSScriptRoot "..\..\..\artifacts\installer\acceptance-logs"
            New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
            Copy-Item -LiteralPath $logPath -Destination (Join-Path $artifactDir "acceptance-setup.log") -Force
        }
        throw "setup exit $($proc.ExitCode) for $Exe"
    }
    Assert-InnoSetupLogNoFailedRuns -LogPath $logPath
    Assert-InstallerServerServicesRunning
    Wait-InstallerServerApiPort
}

$withOpen = $Mode -eq "WithOpenSessions"
Write-Host "B-2 ($Mode): low=$LowVersion high=$HighVersion"

if (-not (Test-Path "C:\ProgramData\Checkpoint\config\server.json")) {
    Install-ServerSetup -Exe $LowSetup -Accounts $AccountsFile -FromVer ""
}

. (Join-Path $PSScriptRoot "Seed-InstallerAcceptanceData.ps1") -AccountsFile $AccountsFile -WithOpenSessions:$withOpen

$before = Get-InstallerDbCounts
$secrets = Get-ServerSecretsFingerprint
Write-Host "B-2 ($Mode): fingerprint PostgreSQL\bin + pgdata before high upgrade"
$pgBinBefore = Get-InstallerPostgreSqlBinFingerprint
$pgDataListingBefore = Get-InstallerPgDataStableListingHash

Write-Host "B-2 ($Mode): silent high upgrade install"
Install-ServerSetup -Exe $HighSetup -Accounts $AccountsFile -FromVer $LowVersion

$after = Get-InstallerDbCounts
$secretsAfter = Get-ServerSecretsFingerprint
$pgBinAfter = Get-InstallerPostgreSqlBinFingerprint
$pgDataListingAfter = Get-InstallerPgDataStableListingHash

Assert-InstallerServerHealthOk

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
    $down = Start-Process -FilePath $LowSetup -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") -Wait -PassThru
    if ($down.ExitCode -eq 0) {
        throw "B-2: downgrade must not succeed silently"
    }
}

Write-Host "B-2 ($Mode): ok"