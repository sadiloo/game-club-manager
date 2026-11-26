# Must match ServerWizard.inc.iss / KioskWizard.inc.iss (Inno 0–5 = built-in; Checkpoint 128+).
$script:CheckpointSetupExitDowngradeBlocked = 128
$script:CheckpointSetupExitPostinstallFailed = 129
$script:CheckpointSetupExitExistingDataKept = 130
$script:CheckpointSetupExitOpenSessionsSilent = 131
$script:CheckpointSetupExitInvalidAccounts = 132
$script:CheckpointSetupExitFailWebview2 = 133
$script:CheckpointSetupExitFailReinstallKeptPg = 134
$script:CheckpointSetupExitFailDatabase = 135
$script:CheckpointSetupExitFailAccountsPhase = 136
$script:CheckpointSetupExitFailServices = 137
$script:CheckpointSetupExitFailFirewall = 138
$script:CheckpointSetupExitFailBackup = 139
$script:CheckpointSetupExitFailStart = 140
$script:CheckpointSetupExitFailUpgradePins = 141
$script:CheckpointSetupExitFailUpgradeStop = 142
$script:CheckpointSetupExitFailUpgradeBackup = 143
$script:CheckpointSetupExitFailUpgradeStart = 144
$script:CheckpointSetupExitFailKioskPostinstall = 150

$script:CheckpointPostinstallPhaseExit = @{
    webview2          = $script:CheckpointSetupExitFailWebview2
    "reinstall-kept-pg" = $script:CheckpointSetupExitFailReinstallKeptPg
    database          = $script:CheckpointSetupExitFailDatabase
    accounts          = $script:CheckpointSetupExitFailAccountsPhase
    services          = $script:CheckpointSetupExitFailServices
    firewall          = $script:CheckpointSetupExitFailFirewall
    backup            = $script:CheckpointSetupExitFailBackup
    start             = $script:CheckpointSetupExitFailStart
    "upgrade-pins"    = $script:CheckpointSetupExitFailUpgradePins
    "upgrade-stop"    = $script:CheckpointSetupExitFailUpgradeStop
    "upgrade-backup"  = $script:CheckpointSetupExitFailUpgradeBackup
    "upgrade-start"   = $script:CheckpointSetupExitFailUpgradeStart
}

function Get-CheckpointPostinstallPhaseExitCode {
    param([Parameter(Mandatory)][string] $InstallPhase)
    if ($script:CheckpointPostinstallPhaseExit.ContainsKey($InstallPhase)) {
        return [int]$script:CheckpointPostinstallPhaseExit[$InstallPhase]
    }
    return [int]$script:CheckpointSetupExitPostinstallFailed
}