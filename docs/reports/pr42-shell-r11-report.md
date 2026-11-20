# PR42 R11 — full report

## Staging

| Field | Value |
|-------|-------|
| **Branch** | `cursor/pr42-r11-staging` |
| **Parent** | `11223994a82f03c5c00c48d5c3f52866b87f9dc2` |
| **HEAD (watched)** | `fa7f332e1dd24d67ec55a032f49bfe816bd96870` |
| **Diff vs parent** | 7 files, +208 / −26 |

## Local gate (pre-CI)

`Release -p:EnableWindowsTargeting=true` — **0 errors** on Server, Shell, Shell.Windows.IntegrationTests, GameClub.Server.Tests.

## CI run [44650203381](https://github.com/sadiloo/game-club-manager/actions/runs/44650203381)

| Job | Conclusion |
|-----|------------|
| ubuntu net10 | **success** |
| windows net48 and net10 | **success** |
| windows-2022 kiosk integration | **success** |
| **windows-2022 shell** | **failure** |
| Workflow overall | **failure** |

No infra flake observed (PostgreSQL install succeeded; shell job ran full ~14 min test suite). **No failed-job rerun.**

### Shell integration (47 tests)

| | Count |
|---|------|
| **Passed** | **38** |
| **Failed** | **9** |
| **Total** | 47 |

**Failed tests**

1. `shell_shows_persian_offline_when_service_stopped` — WebView body missing `سرور` (default “can’t reach this page” for loopback).
2. `shell_shows_persian_offline_after_service_stops_post_webview_ready` — same class.
3. `shell_offline_page_clears_when_service_returns_to_running`
4. `shell_page_alert_still_shows_webview_default_dialog`
5. `shell_window_json_restores_size_position_maximized` — GEOM Loaded (see below).
6. `shell_window_json_infinity_input_round_trips_to_default_size_and_valid_json` — GEOM Loaded (see below).
7. `shell_poller_tick_completing_after_main_window_close_does_not_touch_tray_or_panel` — `NavigationStarting` after close (`actual=1`).
8. `shell_poller_skips_reentrant_tick_during_slow_snapshot_and_one_unexpected_toast`
9. `shell_tray_initiated_stop_shows_no_unexpected_stop_notification` — `UnexpectedStopCount` expected 0, actual 1.

**R11 visibility wins:** post-test trace dump ran; `shell_blocks_external_navigation_and_new_window_with_log_line` **passed** (redaction). Job completed within 45 min (was not cancelled).

### Per-test duration (console `[N s]` / `[N m N s]`)

**Top 10 slowest**

| Duration | Result | Test |
|----------|--------|------|
| 10 m 15 s | Passed | `shell_events_do_not_change_open_session_amount_after_ten_minutes` |
| 47 s | Failed | `shell_offline_page_clears_when_service_returns_to_running` |
| 24 s | Failed | `shell_page_alert_still_shows_webview_default_dialog` |
| 15 s | Passed | `shell_standard_user_cannot_stop_service` |
| 15 s | Failed | `shell_poller_skips_reentrant_tick_during_slow_snapshot_and_one_unexpected_toast` |
| 13 s | Passed | `shell_new_window_blocked_log_redacts_host_in_shell_log` |
| 12 s | Passed | `shell_no_answer_to_stopped_shows_exactly_one_toast` |
| 12 s | Passed | `shell_tray_icon_maps_stopped_start_pending_and_running_to_red_yellow_green_with_persian_tooltip` |
| 12 s | Passed | `shell_unexpected_service_stop_shows_exactly_one_notification` |
| 6 s | Failed | `shell_poller_tick_completing_after_main_window_close_does_not_touch_tray_or_panel` |

**Hang finding:** No inter-result gap **> 60 s** where the following test’s own reported duration is **< 60 s**. The only gap **> 120 s** is **615.6 s** between `shell_new_window_blocked_log_redacts_host_in_shell_log` and `shell_events_do_not_change_open_session_amount_after_ten_minutes`, matching the latter’s **10 m 15 s** billed wait (expected, not a stall between tests).

### SHELL-OFFLINE-TRACE / SHELL-RACE-NAV (job log dump)

**Around offline / WebView tests** (`01:31:22` – `nav-completed-fallback` path when service stopped):

```
SHELL-OFFLINE-TRACE … branch=nav-completed-fallback reason=IsSuccess=False webError=Unknown offlineState=stopped lastUri=127.0.0.1/operator/ uri=127.0.0.1/operator/
SHELL-OFFLINE-TRACE … branch=present-offline-enter reason=offlineState=stopped uri=127.0.0.1/operator/
SHELL-RACE-NAV … closed=True caller=PresentOffline.NavigateToString …
SHELL-OFFLINE-TRACE … branch=present-offline-navigated reason=offlineState=stopped uri=127.0.0.1/operator/
SHELL-OFFLINE-TRACE … branch=nav-completed-skip reason=offlineAlreadyShown=true uri=127.0.0.1/operator/
```

**Around poller-offline / deferred loop** (`01:33:16` – matches failing Persian-offline / tray-reload tests):

```
SHELL-OFFLINE-TRACE … branch=poller-show-offline reason=operatorAnswers=false visual=Stopped icon=Red uri=
SHELL-OFFLINE-TRACE … branch=show-offline-deferred reason=webViewReady=false offlineState=stopped uri=
SHELL-OFFLINE-TRACE … branch=present-offline-enter reason=offlineState=stopped uri=scheme=about host_hash8=e3b0c442
SHELL-RACE-NAV … closed=False caller=PresentOffline.NavigateToString …
```

**Around race test** (`shell_poller_tick_*`; `NavigationStarting` after close):

```
SHELL-RACE-NAV … closed=False caller=NavigatePanel.Source …
SHELL-RACE-NAV … closed=True caller=NavigatePanel.Source …
```

(`shell_blocks_external_navigation` passed; no raw `example.com` in aggregated default log.)

### GEOM / infinity (console `SHELL-GEOM` + assert messages)

**placement-restore** (`shell_window_json_restores_size_position_maximized` — **fail**)

| Phase | state | L,T,W,H | workArea |
|-------|-------|---------|----------|
| PreShow | Normal | NaN,NaN,NaN,NaN | 1024×728 |
| Loaded | Maximized | 120,80,1024,**640** | 1024×728 |
| SourceInitialized | Maximized | 120,80,1024,**700** | 1024×728 |
| Show | Maximized | 120,80,1040,744 | 1024×728 |

Assert (now surfaced): `RestoreBounds.Height` **Expected 700, Actual 640** —  
`RestoreBounds=(120,80,1024,640) Width=1024 Height=640 ActualW/H=1024/640 WindowState=Maximized workAreaW=1024 workAreaH=728`.

**infinity-round-trip** (`shell_window_json_infinity_input_round_trips_*` — **fail**)

| Phase | state | L,T,W,H |
|-------|-------|---------|
| Loaded | **Normal** | 0,0,1024,640 |
| SourceInitialized | Normal | 0,0,1024,728 |
| Show | Normal | 0,0,1024,728 |

Assert: `WindowState` **Expected Maximized, Actual Normal** —  
`RestoreBounds=(0,0,1024,640) … workAreaW=1024 workAreaH=728`.

### Trace → source mapping (proposed product diffs — **not applied**)

| Trace branch / RACE caller | File:line | What traces show |
|----------------------------|-----------|------------------|
| **`poller-show-offline`** | `MainWindow.xaml.cs:84-87` | Poller sees `!OperatorAnswers` → `ShowOfflineAsync`; paired with **`show-offline-deferred`** when `webViewReady=false`. |
| **`show-offline-deferred`** | `MainWindow.xaml.cs:362-367` | Offline deferred; tests still see WebView network error text instead of Persian offline HTML. |
| **`nav-completed-fallback`** | `MainWindow.xaml.cs:188-191` | Loopback nav failure → `PresentOfflinePageAsync`; worked in one window (`127.0.0.1/operator/` uri). |
| **`NavigatePanel.Source`** | `MainWindow.xaml.cs:340-341` | Race log with **`closed=True`** after `MainWindow` close → matches `NavigationStarting` after close. |
| **`PresentOffline.NavigateToString`** | `MainWindow.xaml.cs:398-399` | Offline HTML path; used from poller and nav-completed. |

**Proposed OFFLINE diff** (`MainWindow.xaml.cs` — held until PO approves):

```diff
     private async Task ShowOfflineAsync(ServerStateSnapshot snap)
     {
         ...
         if (!_webViewReady)
         {
             LogOfflineTrace("show-offline-deferred", $"webViewReady=false offlineState={state}");
             _pendingOffline = true;
             _pendingOfflineState = state;
+            _ = EnsureWebViewAsync();
             return;
         }
```

**Proposed RACE diff** (`MainWindow.xaml.cs`):

```diff
     public Task NavigatePanelAsync()
     {
+        if (_closed)
+        {
+            return Task.CompletedTask;
+        }
         if (!_webViewReady)
```

And gate `PanelWebView.Source = uri` in `OnPollerSnapshotChanged` (`MainWindow.xaml.cs:72-75`) with the same `_closed` check immediately before assignment (dispatcher may run after `OnClosed`).

### Tray unexpected-stop (proposal only — **not applied**)

**CI:** `shell_tray_initiated_stop_shows_no_unexpected_stop_notification` — `UnexpectedStopCount` **1** vs **0** (`ShellTrayStatusTests.cs:111`).

**Production path:** tray stop sets `ShellSeams.TrayStopInProgress = true` at `ShellTrayHost.cs:103` before elevated `--server-control stop`; poller suppresses toast at `ServerStatePoller.cs:65-74` only while flag is true. Flag clears only on `Process.Start` failure (`ShellTrayHost.cs:118`), never on successful elevated stop completion.

**Proposed diff** (`ServerStatePoller.cs` after `BuildSnapshot()`):

```diff
         var snapshot = BuildSnapshot();
+        if (ShellSeams.TrayStopInProgress
+            && snapshot.VisualState == ShellServerVisualState.Stopped)
+        {
+            ShellSeams.TrayStopInProgress = false;
+        }
         if (_armedForUnexpectedToast
             && _wasServiceRunningClass
             && snapshot.IconState == TrayIconState.Red
             && snapshot.IsUnexpectedStopRed
             && !ShellSeams.TrayStopInProgress
```

**Optional (stronger):** child `ServerControlProgram` writes `%ProgramData%\Checkpoint\logs\tray-stop.flag` on intentional `stop`; poller treats like `MaintenanceFlagPresent` (`ServerStatePoller.cs:99-120`).

## R11 deliverables status

| Item | Status |
|------|--------|
| CI trace copy + job echo | Done |
| URI redaction in OFFLINE traces | Done — blocks-external test **passes** |
| GEOM assert surfacing | Done — real height/state values in log |
| Shell job 45 min + per-test console times | Done |
| Product fixes (offline / race / tray / GEOM) | **Not in scope** — proposals above only |