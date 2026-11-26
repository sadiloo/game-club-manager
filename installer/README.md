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
| 132 | Invalid `/ACCOUNTS` (blank username or PIN, a role that is not a role name, no Manager, or fewer than three accounts) |
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

Failed first install: Setup stops CheckpointServer and CheckpointPostgres (`CHECKPOINT_POSTINSTALL_ABORT_SERVICES_STOPPED` from PowerShell, `CHECKPOINT_SETUP_FRESH_ABORT_SERVICES_STOPPED` from Setup). Fix the cause and run Setup again.

The server never creates default PINs on an installed server: `InitialUsers` (written by the `accounts` phase) must contain at least one Manager, roles are names only (`Owner`, `Manager`, `Operator`; numbers such as `0` or `1` are refused), and an empty `InitialUsers` makes the server refuse to start seeding instead of falling back to 1234/1111.

## Recovery after a failed upgrade (MVP)

There is **no binary rollback** in this release (post-MVP). When an upgrade fails and the log shows `CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED`, Setup also logs the exact command to run, for example:

```
CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED recovery_command="D:\CheckpointServerSetup-1.2.3.exe" /SKIPSESSIONCHECK=1 /LOG="C:\ProgramData\Checkpoint\logs\setup-recovery.log"
```

Pick one:

1. **Rerun the new installer** once the cause in the log is fixed: run the `recovery_command=` line from the log as Administrator (`"<new CheckpointServerSetup.exe>" /SKIPSESSIONCHECK=1 /LOG="C:\ProgramData\Checkpoint\logs\setup-recovery.log"`). It takes its own pre-upgrade backup before migrating.
2. **Go back to the previous version**: uninstall (the data folder stays), install the previous `CheckpointServerSetup` (it reuses the kept PostgreSQL data, `CHECKPOINT_REINSTALL_KEPT_PGDATA`, after taking a `pre-reinstall-kept-*.dump`), then restore the last dump from before the upgrade (`C:\ProgramData\Checkpoint\backups\pre-upgrade-<oldVersion>-<stamp>.dump`, or the newest `checkpoint-*.dump`) with `powershell -ExecutionPolicy Bypass -File "C:\Program Files\Checkpoint\Server\tools\restore.ps1" -DumpPath "<dump>"`.

Backups are never deleted by Setup or by the backup task; a failed `pg_dump` removes only the partial or empty file it wrote itself in that run (`CHECKPOINT_PGDUMP_FAILED ... action=removed_own_failed_output`), and a new dump never reuses an existing file name.

### `/SKIPSESSIONCHECK=1` (operator recovery flag)

Kept in release builds on purpose. It skips the open-session check on upgrade (silent: exit 131; interactive: the open-sessions question). Use it only for recovery, when CheckpointServer is stopped and the open-session count cannot be read, or when you have closed the sessions yourself: open sessions are **not** protected in that run. Every use is logged: `CHECKPOINT_SKIPSESSIONCHECK used=1 value=1 context=silent-upgrade|interactive-upgrade ...`.

### Kiosk

| Code | Meaning |
|------|---------|
| 0 | Success |
| 150 | Post-install `kiosk-postinstall.ps1` failed |

Release installers must not ship internal test-hook code (see `installer/tests/ServerPayloadStatic.Tests.ps1`).