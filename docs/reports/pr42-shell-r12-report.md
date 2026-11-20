# PR42 R12 — report

## Staging

| Field | Value |
|-------|-------|
| **Branch** | `cursor/pr42-r12-staging` |
| **HEAD (gate)** | `7abe62c9f90796adc5d682c3bceb70bcc448611a` |
| **Parent (gate)** | `f4f2bc7f985f42f85129ee603c2ec205fef3aa82` |

## GEOM fix (applied)

`WindowPlacementStore.OnLoadedOnce`: `ApplyBounds()` immediately before `WindowState = Maximized` (maximize stays on Loaded per R6).

## CI run [44656623938](https://github.com/sadiloo/game-club-manager/actions/runs/44656623938)

Triggered by push to `cursor/pr42-r12-staging` at `7abe62c` (no `workflow_dispatch` needed).

| Job | Conclusion |
|-----|------------|
| windows net48 and net10 | **success** |
| windows-2022 kiosk integration | **success** |
| ubuntu net10 | **success** |
| **windows-2022 shell** | **failure** |
| Workflow overall | **failure** |

No job rerun.

### Shell integration

| | Count |
|---|------|
| **Total** | **49** |
| **Passed** | **43** |
| **Failed** | **6** |

**Failed tests (message)**

1. `shell_shows_persian_offline_when_service_stopped` — `Assert.Contains`: `String: ""`, not found `"سرور"`.
2. `shell_offline_page_clears_when_service_returns_to_running` — `Persian offline page was not shown while service stopped`.
3. `shell_page_alert_still_shows_webview_default_dialog` — `operator page did not finish navigation`.
4. `shell_shows_persian_offline_after_service_stops_post_webview_ready` — `Assert.Contains`: body has Edge `Hmmm… can't reach this page` / `127.0.0.1`, not found `"سرور"`.
5. `shell_poller_skips_reentrant_tick_during_slow_snapshot_and_one_unexpected_toast` — `expected at least one skipped reentrant tick; skipped=0`.
6. `shell_window_json_infinity_input_round_trips_to_default_size_and_valid_json` — Loaded: `Expected: Maximized`, `Actual: Normal`; `RestoreBounds=(0,0,1024,640)`.

### Focused outcomes (7abe62c)

| Area | Test | Result |
|------|------|--------|
| **Tray (3)** | `shell_tray_initiated_stop_shows_no_unexpected_stop_notification` | **Pass** (10 s) |
| | `shell_tray_stop_then_green_then_unexpected_stop_shows_exactly_one_toast` | **Pass** (24 s) |
| | `shell_tray_stop_declined_uac_then_unexpected_stop_shows_exactly_one_toast` | **Pass** (6 s) |
| **GEOM (2)** | `shell_window_json_restores_size_position_maximized` (R6 / placement-restore) | **Pass** (94 ms); Loaded `state=Maximized` `width=1024` `height=700` |
| | `shell_window_json_infinity_input_round_trips_to_default_size_and_valid_json` | **Fail** — see above |
| **Offline (4)** | `shell_shows_persian_offline_when_service_stopped` | **Fail** |
| | `shell_shows_persian_offline_after_service_stops_post_webview_ready` | **Fail** |
| | `shell_offline_page_clears_when_service_returns_to_running` | **Fail** |
| | `shell_page_alert_still_shows_webview_default_dialog` | **Fail** |
| **Race (2)** | `shell_poller_tick_completing_after_main_window_close_does_not_touch_tray_or_panel` | **Pass** (6 s); `navDelta=0` |
| | `shell_poller_skips_reentrant_tick_during_slow_snapshot_and_one_unexpected_toast` | **Fail** |

### Trace evidence (job log)

**SHELL-GEOM — placement-restore (pass)**

```
SHELL-GEOM test=placement-restore phase=Loaded ... state=Maximized left=120 top=80 width=1024 height=700 ...
SHELL-GEOM test=placement-restore phase=SourceInitialized-after-ApplyBounds ... state=Maximized ... height=700
```

**SHELL-GEOM — infinity-round-trip (fail at Loaded)**

```
SHELL-GEOM test=infinity-round-trip phase=Loaded ... state=Normal left=0 top=0 width=1024 height=640 ...
SHELL-GEOM test=infinity-round-trip phase=SourceInitialized-after-ApplyBounds ... height=728
```

**SHELL-TRAY-TRACE**

- Harness: `TrayStopInProgress=False caller=FakeShellSeams.ResetHarnessStatics` at each `RunAsync` reset.
- `pollerTick pollerId=6` / `7` during long offline-poll tests; all three tray tests **passed** (no unexpected-stop assert failures).

**SHELL-OFFLINE-TRACE** (post-test dump; deferred path works in product tests that use MainWindow)

```
branch=poller-show-offline ... visual=Stopped icon=Red
branch=show-offline-deferred ... webViewReady=false offlineState=stopped
branch=present-offline-enter ... offlineState=stopped uri=scheme=about ...
branch=present-offline-navigated ...
branch=nav-completed-skip ... offlineAlreadyShown=true
```

Earlier navigations when service up: `nav-completed-skip ... IsSuccess=True ... lastUri=127.0.0.1/operator/` (Persian offline integration tests still saw empty body or Edge default page).

**SHELL-RACE-NAV / SHELL-RACE** (close race **pass**)

```
SHELL-RACE ... MainWindow._closed=True ... navDelta=0 panelNavigateAfterClose=False
SHELL-RACE-NAV ... closed=False caller=NavigatePanel.Source (no closed=True NavigatePanel.Source in assert window)
```

## Tray / src notes (R12)

- `TrayStopObserved`: clear flag only after a non-Running/Green snapshot, then recovery (StartPending / Running / Green), never before toast check.
- Test (i): `OnTickEntered` wait while Running with flag; test (iii): `StartElevatedProcess` → `Win32Exception(1223)` via `ShellTrayHost.LaunchServerControl("stop")`.
- Menu handlers call `LaunchServerControl` directly (`internal`); no `*ForTests` in production UI.