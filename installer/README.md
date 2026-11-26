# Checkpoint installers (server + kiosk)

Build and acceptance run on Windows CI (`scripts/windows/ci-installer.ps1`). Release payloads stage under `artifacts/installer/`; internal **Hooks** installers (`CheckpointInternalTestHooks=1`) are separate artifacts for acceptance only.

Persian operator guides: [server install](../docs/install/server-install.fa.md), [kiosk install](../docs/install/kiosk-install.fa.md).

## Setup.exe exit codes

### Server

| Code | Meaning |
|------|---------|
| 0 | Success |
| 128 | Downgrade blocked (installed version newer than setup) |
| 129 | Post-install failure fallback (specific phases use 133–144) |
| 130 | Fresh install refused: existing PostgreSQL data kept |
| 131 | Silent upgrade refused: open sessions or **unknown** open-session count (server unreachable); log line `CHECKPOINT_OPEN_SESSIONS_SILENT reason=open_session_count_unknown_server_unreachable` |
| 132 | Invalid `/ACCOUNTS` (blank username, unknown role, or fewer than three accounts) |
| 133 | Post-install: `webview2` |
| 134 | Post-install: `reinstall-kept-pg` |
| 135 | Post-install: `database` |
| 136 | Post-install: `accounts` |
| 137 | Post-install: `services` |
| 138 | Post-install: `firewall` |
| 139 | Post-install: `backup` |
| 140 | Post-install: `start` |
| 141 | Upgrade: `upgrade-pins` |
| 142 | Upgrade: `upgrade-stop` |
| 143 | Upgrade: `upgrade-backup` |
| 144 | Upgrade: `upgrade-start` |

On the first failed post-install phase, later phases do not run. Details: `C:\ProgramData\Checkpoint\logs\checkpoint-setup-failure.ini`, `postinstall-phase-trace.log`, Inno `/LOG=`.

Failed upgrade: the upgraded files are already installed and CheckpointServer migrates the database when it starts. If this run's pre-upgrade backup exists (`upgrade-backup` succeeded; marker `logs\preupgrade-dump.current`), Setup starts CheckpointServer again (`CHECKPOINT_POSTINSTALL_UPGRADE_RESTORE start CheckpointServer dump=…`). If it does not exist (failure in `upgrade-pins`, `upgrade-stop` or `upgrade-backup`), CheckpointServer is left **stopped** and the database is not migrated (`CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED reason=no_preupgrade_backup`). Operator: fix the cause shown in the Setup log and run the same Setup again; do not start CheckpointServer by hand.

### Kiosk

| Code | Meaning |
|------|---------|
| 0 | Success |
| 150 | Post-install `kiosk-postinstall.ps1` failed |

Release installers must not ship internal test-hook code (see `installer/tests/ServerPayloadStatic.Tests.ps1`).