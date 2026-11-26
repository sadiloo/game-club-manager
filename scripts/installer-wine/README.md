# Installer behaviour legs on Linux (wine)

Box-side harness that pins what the Windows acceptance job (`scripts/windows/ci-installer.ps1 -Phase Acceptance`) pins on a real VM, without Windows:

| Script | What it proves |
|---|---|
| `Invoke-WineInstallerLegs.sh` | Compiles all four installers (server release/hooks, kiosk release/hooks) with Inno Setup 6.7.1 under wine against stub payloads, then runs them silently with a fake `powershell.exe` (`fakeps.c`) and asserts: per-phase exit codes (135/132/136/140/141/142/143/144/150), stop at the first failed phase, PowerShell output in the Inno log, the 131 silent open-session guard, **F6**: an upgrade that fails before the pre-upgrade dump exists (upgrade-pins / upgrade-stop / upgrade-backup, and a stale marker from an earlier run) logs `CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED` and never starts the server binary (so the database is never migrated), and an upgrade that fails **after** the dump restores (`CHECKPOINT_POSTINSTALL_UPGRADE_RESTORE start CheckpointServer dump=…`) and starts it. |
| `Invoke-BoxPostinstallUpgradeRestore.ps1` | Runs `installer/tools/server-postinstall.ps1` under PowerShell 7 on Linux (PSDrive `C:` → temp root, Start/Stop-Service stubbed) and asserts the PowerShell side of F6 (no dump → left stopped, no Start-Service, trace `upgrade_restore=left_stopped`; empty dump ≠ backup; dump → restore start) and that both test hooks write `status=fail` to the phase trace. |

`stubserver.c` is installed as `GameClub.Server.exe` and as the CheckpointServer service binary; it appends its command line to `C:\checkpoint-server-exec.log`. Any line other than `--check-default-pins` means the server was started (on a real install that is the moment EF migrations run).

## Run

Requirements: `wine` (32- and 64-bit), `mingw-w64` (`i686-w64-mingw32-gcc`, `x86_64-w64-mingw32-gcc`), PowerShell 7, and a wine prefix with Inno Setup 6.7.1 installed at `C:\is` (`wine innosetup-6.7.1.exe /VERYSILENT /DIR=C:\is`).

```bash
ISCC_PREFIX=/path/to/prefix scripts/installer-wine/Invoke-WineInstallerLegs.sh /tmp/wine-legs   # exits non-zero on any FAIL
pwsh -NoProfile -File scripts/installer-wine/Invoke-BoxPostinstallUpgradeRestore.ps1          # exits 1 on any FAIL
```

The legs copy `ISCC_PREFIX` per case and never touch it. On Windows CI the same F6 rule is pinned by `installer/tests/UpgradeRestoreNoDumpStatic.Tests.ps1` (static) and by `Assert-InstallerUpgradeLeftStoppedNoDump` in the R8f acceptance legs (real PostgreSQL: service stopped, no dump, `__EFMigrationsHistory` unchanged).

This lives outside `installer/` on purpose: `installer/tests/NoPwshInstaller.Tests.ps1` forbids PowerShell 7 in installer scripts.
