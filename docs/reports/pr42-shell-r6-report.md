# PR #42 shell R6 report (staging)

Parent: `4999323d9a1dce2e5ac3e2507fc3a5d156eeecb6` (`cursor/sub-round-a-shell-f022`).

## Compile gate (local)

Release with `-p:EnableWindowsTargeting=true` on `GameClub.Server`, `GameClub.Shell`, `GameClub.Shell.Windows.IntegrationTests`, `GameClub.Server.Tests` — **0 errors**.

## Summary

| Area | Files |
|------|-------|
| Src (allowed only) | `MainWindow.xaml.cs`, `WindowPlacementStore.cs` |
| Harness | `WpfStaApplicationFixture.cs`, `ShellPlanAIntegrationTests.cs`, `ShellWindowPlacementRegressionTests.cs`, `ShellPollerReentrancyTests.cs` |

Staging sha: *(filled after push)*

## Items

1. **A3 minutes sync** — `ShellPlanAIntegrationTests.cs`: `WaitUntilBothSessionsShareMinutesPlayed` before amount compares (`:206`, `:217`). Assertions unchanged.
2. **Poller / tray harness** — `WpfStaApplicationFixture.cs:82-100`: `ForceExit` + `MainWindow.ExitShell()` in `CloseAllWindows`. Assertions unchanged.
3. **#8–10 width** — Tests use `Math.Min(saved, workArea)` with runner 1024×728 comment; maximized asserts unchanged. Src: `WindowPlacementStore` clamps width/height to work area.
4. **Maximized** — `WindowPlacementStore.cs`: bounds in `SourceInitialized`, `Maximized` once in `Loaded`.
5. **Offline** — `MainWindow.xaml.cs`: `_offlineShown`, panel navigate only when `Running`, recovery on Running, nav logs, remove 5s post-offline delay. New test `shell_offline_page_clears_when_service_returns_to_running`.
6. **page_alert** — Wait operator `NavigationCompleted` success, then `await ExecuteScriptAsync(alert)`.
7. **new-window-blocked** — Redacted log in src; new test `shell_new_window_blocked_log_redacts_host_in_shell_log`.
8. **Reentrancy** — `ShellPollerReentrancyTests.cs:44` `BuildSnapshotForTests()` after slow tick setup.

## Assertion edits

Only **#8** and **#10** width/height expected values (work-area clamp). Maximized assertion text unchanged.

## Full src diff

See `git diff 4999323d9a1dce2e5ac3e2507fc3a5d156eeecb6 -- src/GameClub.Shell/MainWindow.xaml.cs src/GameClub.Shell/Shell/WindowPlacementStore.cs`.