# Must match ServerWizard.inc.iss (Inno codes 0 success, 1 init failure, 2 user cancel, 3/4 fatal, 5=need admin).
$script:CheckpointSetupExitDowngradeBlocked = 128
$script:CheckpointSetupExitPostinstallFailed = 129
$script:CheckpointSetupExitExistingDataKept = 130
$script:CheckpointSetupExitOpenSessionsSilent = 131
$script:CheckpointSetupExitInvalidAccounts = 132

# Process exit codes from server-postinstall.ps1 (mapped by Setup via checkpoint-setup-failure.ini).
$script:CheckpointPostinstallProcessExitInvalidAccounts = 40