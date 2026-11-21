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

---

## R7 (test-only harness cascade fix)

Parent: `32c9ad98acea1650022ef45ed4f509d71d80ea5e`.

Staging sha: `afb703ccae8cef8131275a44f0b08681d8dac47b`

### Cause

CI run `44601236174` (`windows-2022 shell`): job **36/46** failed as a **cascade**, not a product regression. `WpfStaApplicationFixture.CloseAllWindows` set `ShellEnvironment.ForceExit = true` then called `MainWindow.ExitShell()` (`MainWindow.xaml.cs:334-338`), which runs `Application.Current.Shutdown()`. That killed the shared STA `Application` for the whole `WpfStaApplicationCollection`. Later tests hit `TaskCanceledException` at `WpfStaApplicationFixture.RunAsync` (`InvokeAsync` / `Task.Wait`).

First hard failure in that job: `shell_new_window_blocked_log_redacts_host_in_shell_log`; many following tests (including `shell_offline_page_clears_when_service_returns_to_running` at `:543` in **2 ms**) failed the same way.

### Fix (tests only)

| Change | File |
|--------|------|
| `CloseAllWindows`: keep `ForceExit = true`, call `window.Close()` for all windows (including `MainWindow`); `OnClosing` allows close when `ForceExit`; `OnClosed` disposes poller/flags/tray — no `Shutdown()` | `WpfStaApplicationFixture.cs` |
| Replace fixed `Task.Delay(4000)` with ≤30 s poll until `CoreWebView2 != null`; `Assert.NotNull` unchanged | `ShellPlanAIntegrationTests.cs` |
| `RunAsync`: if dispatcher shutdown started/finished, throw `InvalidOperationException` naming `_shutdownCausingTestName` recorded on `Application.Exit`; `[CallerMemberName]` on each run | `WpfStaApplicationFixture.cs` |

Compile gate (local): Release + `-p:EnableWindowsTargeting=true` on Server, Shell, Shell.Windows.IntegrationTests, Server.Tests — **0 errors**.

### Offline no-flicker (R6 behaviour, R7 evidence)

**Condition (src, unchanged in R7):** `RunningRecover` navigation runs only when `snap.VisualState == ShellServerVisualState.Running` and `_offlineShown` (`MainWindow.xaml.cs:50-58`). `VisualState.Running` in `ServerStatePoller` requires the Windows service **Running** *and* loopback health `dbOk` (`ServerStatePoller.cs:161-171`). While the service is stopped or health fails, poller stays non-Running / `OperatorAnswers == false` → offline path only. `NavigatePanelAsync` also bails when `_offlineShown` or `VisualState != Running` (`MainWindow.xaml.cs:254-262`), so no operator panel navigation between offline ticks.

**`panel-navigate` log lines** (`ShellFileLog` → `%ProgramData%` `shell-*.log`, not stdout):

| Caller | When |
|--------|------|
| `panel-navigate caller=PresentOffline scheme=data …` | Offline HTML (`PresentOfflinePageAsync`) |
| `panel-navigate caller=RunningRecover scheme=http …` | Recovery after Running + health |
| `panel-navigate caller=NavigatePanel scheme=http …` | Normal panel load (not while `_offlineShown` / not Running) |

**CI run `44601236174`:** `SHELL_LIST_TEST` lists `shell_offline_page_clears_when_service_returns_to_running`, but the recorded failure is immediate (`2 ms`, `TaskCanceledException` at line 543) — cascade from shared app shutdown, not an assertion that the webview showed a panel/error page between offline polls. After R7, a green shell job should execute that test long enough to poll for offline Persian text, restart the service, and assert operator URL without `سرور` in body (no flicker).

No src changes in R7.