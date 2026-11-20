# PR42 R11 — gate report (awaiting `GATE OK <sha>`)

## Staging

| Field | Value |
|-------|-------|
| **Branch** | `cursor/pr42-r11-staging` |
| **Parent** | `11223994a82f03c5c00c48d5c3f52866b87f9dc2` |
| **Staging SHA** | 9a25e71fc05783b2d7e268f13ce165f704b80f51 |

## Diff stat

9a25e71fc05783b2d7e268f13ce165f704b80f51

## Gate

`Release -p:EnableWindowsTargeting=true`, four projects — **0 errors**:

- `src/GameClub.Server/GameClub.Server.csproj`
- `src/GameClub.Shell/GameClub.Shell.csproj`
- `tests/GameClub.Shell.Windows.IntegrationTests/GameClub.Shell.Windows.IntegrationTests.csproj`
- `tests/GameClub.Server.Tests/GameClub.Server.Tests.csproj`

## Scope implemented

- **a)** `scripts/windows/ci-shell.ps1`: post-test `finally` copies `%LOCALAPPDATA%\Checkpoint\Shell\logs` → `artifacts/shell-windows-logs/`; echoes `SHELL-OFFLINE-TRACE` / `SHELL-RACE-NAV`; throws if logs dir missing or empty.
- **b)** `MainWindow.xaml.cs`: `FormatUriForLog` — loopback `host+path`, else `scheme` + `host_hash8` (same rule as navigation-blocked); applied to OFFLINE trace `uri` / `lastUri` fields.
- **c)** GEOM + infinity: `ShellGeomLoadedWait` — assert exceptions via `TrySetException`, rich Loaded message, `AwaitLoaded` rethrows.
- **d)** `ci.yml`: `windows-2022 shell` job `timeout-minutes` 25 → 45; per-test timing via existing `console;verbosity=normal` logger.

## CI

**Not run** — waiting for `GATE OK <sha>`.

## Tray stop diagnosis (proposal only — not applied)

**Symptom:** `shell_tray_initiated_stop_shows_no_unexpected_stop_notification` — unexpected-stop toast count 1 vs 0 (R8).

**Cause:** `ShellTrayHost.LaunchServerControl` sets `ShellSeams.TrayStopInProgress = true` only in the UI click handler (`ShellTrayHost.cs:99-104`). The elevated `--server-control stop` runs in a **second process** (`App.xaml.cs:16-18`); the main shell’s poller can observe `ServiceControllerStatus.Stopped` on a timer tick **before** the flag is visible across the intended window, or the flag is never cleared on the success path (`ShellTrayHost.cs:118` only on `Process.Start` failure), which can desync later ticks.

**Proposed minimal diff (do not apply until PO approves):**

`src/GameClub.Shell/Shell/ServerStatePoller.cs` — after `BuildSnapshot()`, when `ShellSeams.TrayStopInProgress` is true and status is `StopPending`, `StopPending`, or `Stopped`, skip unexpected toast (existing guard) and clear the flag once `Stopped` is stable so real unexpected stops still toast later:

```diff
         var snapshot = BuildSnapshot();
+        if (ShellSeams.TrayStopInProgress
+            && snapshot.VisualState is ShellServerVisualState.Stopped or ShellServerVisualState.Stopping)
+        {
+            if (snapshot.VisualState == ShellServerVisualState.Stopped)
+            {
+                ShellSeams.TrayStopInProgress = false;
+            }
+        }
         if (_armedForUnexpectedToast
```

Alternative/addition at `ShellTrayHost.cs:99` — write a short-lived `tray-stop.flag` under `ShellPaths.ProgramDataLogs` that `ServerStatePoller` treats like `TrayStopInProgress` (mirrors maintenance flag pattern at `ServerStatePoller.cs:99-120`).

## Open

After `GATE OK`, watch CI on staging head; complete per-test durations, trace lines, OFFLINE/RACE branch naming, and full report sections per brief.