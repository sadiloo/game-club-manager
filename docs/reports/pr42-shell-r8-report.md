# PR #42 shell R8 report (staging)

Parent SHA: `13e5a4ed2137b852e2519809d17ddf8a743ec4d8` (`cursor/sub-round-a-shell-f022`).

## ls-remote

```
11f86d725399549e0a323fe208aaed322a456b04	refs/heads/cursor/pr42-r8-staging
```

## CI run (latest)

- **Run id:** [44625983743](https://github.com/sadiloo/game-club-manager/actions/runs/44625983743)
- **Trigger:** push `cursor/pr42-r8-staging` @ `11f86d7`
- **Prior run:** [44620171740](https://github.com/sadiloo/game-club-manager/actions/runs/44620171740) @ `612191a`

### Per-job results

| Job | Result |
|-----|--------|
| ubuntu net10 | success |
| windows net48 and net10 | success |
| windows-2022 kiosk integration | success |
| **windows-2022 shell** | **failure** — 37 passed / 10 failed / 47 total (~6.0 min) |

### Compile gate (local, this agent)

Release `-p:EnableWindowsTargeting=true`: `GameClub.Server`, `GameClub.Shell`, `GameClub.Shell.Windows.IntegrationTests`, `GameClub.Server.Tests` — **0 errors**.

## Approved src fixes (items 1–2)

1. **`MainWindow.xaml.cs`** — `_closed`, named `OnPollerSnapshotChanged`, early `if (_closed) return` in dispatcher body; `OnClosed` sets `_closed`, unsubscribes handler, then disposes poller/flags/tray.
2. **`ServerStatePoller.cs`** — `volatile _disposed`; `Dispose` sets flag then `_timer.Dispose()`; `Tick` returns early when disposed at entry and again before `Changed?.Invoke`.

No `Timer.Dispose(WaitHandle)` on the UI thread.

## Test-only (items 3–4)

3. **`ShellPollerCloseRaceTests.cs`** — new race test (see below).
4. **`ShellTrayStatusTests.shell_tray_menu_status_line_shows_tehran_start_time_when_running`** — fixed `UtcNow` + `ReadLocalStatus.StartedAtUtc` on same Tehran day; **assert.Equal** `سرور: روشن — از ساعت ۰۹:۰۰` (CI **passed**).

## Race test result

### Run 44625983743 (harness fix @ `11f86d7`)

`shell_poller_tick_completing_after_main_window_close_does_not_touch_tray_or_panel` — **FAILED** (3 s)

```
Assert.Equal() Failure: Expected: 0  Actual: 1
  at ShellPollerCloseRaceTests.cs:97 (compiled lambda; post-close guard assertion)
```

Tick blocking works after moving `BeforeBuildSnapshot` inside `RunAsync` (`11f86d7`). Failure is a **1 vs 0** on one of: unhandled count, tray `TextChanged` after close, or `NavigationStartingCount` delta — not a harness timeout.

### Run 44620171740 @ `612191a`

**FAILED** — `TimeoutException` (hook cleared by `ResetHarnessStatics` before fix).

**Regression check:** Runs `44620171740` and `44625983743` have **no** `NullReferenceException` / `NotifyIcon` lines (run `44610139270` had unhandled NRE at `ShellTrayHost.UpdateSnapshot` during close).

## Item 5 — geometry (diagnose only, unchanged assertions)

### `shell_window_json_restores_size_position_maximized`

**FAILED** — `ShellPlanAIntegrationTests.cs:110` (assert in lambda at ~:105)

```
Assert.Equal() Failure: Values are not within 0 decimal places
Expected: 1024 (rounded from 1024)
Actual:   1040 (rounded from 1040)
```

**Reading:** After `Show()`, width is **1040** while the test expects `Math.Min(1024, area.Width)` with comment CI work area 1024×728 (`:108-110`). Either the runner primary working area width is **1040** (not 1024), or placement restore leaves **Normal** width 1040 before/without the maximized path the assertion assumes at `Show()` time. `WindowPlacementStore.Apply` sets bounds in `SourceInitialized` then `IsMaximized` in `Loaded` (`WindowPlacementStore.cs:44-56`); asserting width at `Show()` may run before `Loaded` applies `Maximized`, so `window.Width` reflects clamped restore width (1040) not work-area cap 1024. **Not changed** (per brief).

### `shell_window_json_infinity_input_round_trips_to_default_size_and_valid_json`

**FAILED** — `ShellWindowPlacementRegressionTests.cs:37`

```
Expected: Maximized
Actual:   Normal
```

**Reading:** JSON requests `IsMaximized: true` with Infinity dims normalized in `Load()` (`WindowPlacementStore.cs:98-108`). `Show()` assertion runs before `Loaded` one-shot sets `WindowState = Maximized` (`WindowPlacementStore.cs:50-56`). Same timing/class of issue as above; **not changed**.

## Item 6 — MONEY (`shell_events_do_not_change_open_session_amount_after_ten_minutes`)

**FAILED** — `ShellPlanAIntegrationTests.cs:828` / call from `:209`

```
System.InvalidOperationException : SHELL-A3 and SHELL-REF did not report the same minutesPlayed within 90s.
```

**Helper:** `WaitUntilBothSessionsShareMinutesPlayed` — logic and **90 s** wait unchanged; **PRINT-ONLY** line per poll added at `:816-817`:

```
SHELL-A3 minutes-sync poll UTC=<iso> shellMinutes=<n|null> refMinutes=<n|null>
```

**Literal timeline (run 44620171740, first polls):**

```
SHELL-A3 minutes-sync poll UTC=2026-11-19T21:47:11.3936788Z shellMinutes=null refMinutes=null
SHELL-A3 minutes-sync poll UTC=2026-11-19T21:47:12.0517217Z shellMinutes=null refMinutes=null
… (≈178 lines, all polls through 21:48:41Z) …
```

**Sample near timeout:** every poll in this run stayed `shellMinutes=null refMinutes=null` for the full 90 s window (operator bill GET never surfaced `minutesPlayed` for either open session in time).

**Reading:** Failure is not amount drift yet — sync predicate never satisfied because **both** sessions' `minutesPlayed` stayed null in live bill JSON for the entire wait (`ShellCiHttp.GetOpenSessionLiveBillAsync` at `ShellCiHttp.cs:58-59`). Prior run `44610139270` failed the same exception without these print lines. `startedAt` was not added to print lines (not present on `OpenBillSnapshot`); would need a separate GET if required on a later pass.

## `shell_offline_page_clears_when_service_returns_to_running`

**Ran:** yes (listed + executed ~48 s). **Passed:** no.

```
Persian offline page was not shown while service stopped
  at ShellPlanAIntegrationTests.cs:585 (Assert.True offlineSeen))
```

**panel-navigate:** no `panel-navigate` lines captured in the GitHub Actions stdout for this job (shell file log lines are not echoed to CI console in this run).

## Other shell failures (same job, for context)

- `shell_shows_persian_offline_when_service_stopped` / `shell_shows_persian_offline_after_service_stops_post_webview_ready` — offline HTML / WebView error page, not embedded `سرور` text.
- `shell_page_alert_still_shows_webview_default_dialog` — operator page did not finish navigation.
- `shell_poller_skips_reentrant_tick_during_slow_snapshot_and_one_unexpected_toast` — timeout (likely shared timing with slow poller in job).
- `shell_tray_initiated_stop_shows_no_unexpected_stop_notification` — unexpected stop count 1 vs 0.

## Tehran test (item 4)

`shell_tray_menu_status_line_shows_tehran_start_time_when_running` — **Passed** (204 ms) with exact line `سرور: روشن — از ساعت ۰۹:۰۰`.