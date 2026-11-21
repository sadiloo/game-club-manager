# PR #42 shell R27 round report (staging)

## CI root cause — silent prune / 10 s navigation loop (05762b6 → dd35b60)

**Staging CI** `44868475549` on `05762b6`, trace root `gcm-shell-afcf38fe80864bceb11bfd1a71c453a9`
(`shell_probe_ok_navigation_timeout_shows_panel_unavailable_without_navigation_loop`).

Sequence:

1. `nav-starting` id=2, `panel-nav-timeout-arm` deadlineSec=10.
2. At ~10s: `panel-nav-timeout-cancel reason=fired` then **`panel-nav-timeout-skip reason=no-pending`**
   (registry row removed by `PruneExpiredLoopbackPanelNavigations` without `HandleRunningPanelNavigationFailure`).
3. New `panel-nav-start` / `NavigatePanel` every ~10s; slow operator response completes later as
   **`nav-completed-orphan`** (ConnectionAborted), never `present-offline-enter`.

**Fix:** `TryConfirmTimedOutLoopbackPanelNavigation` — one `panel-nav-timeout` → failure handler per nav id;
late completion stays `nav-completed-orphan` with `timedOut=true` only logged.

## Empty JSON (`shell_running_with_failing_panel_backs_off_and_avoids_offline_flash_loop`)

**Not** an empty trace file. `JsonReaderException` at `ShellPlanAIntegrationTests.cs` ~902: the measure loop
calls `ExecuteScriptAsync("JSON.stringify({...})")` while the WebView document is still **`about:blank`**.

Same CI root and traces:

- `uri=scheme=about host_hash8=e3b0c442` during the loop (no offline data HTML).
- `panel-nav-timeout-skip reason=no-pending` after prune (no `PresentOffline`).

WebView2 returns empty/non-JSON script output on blank → `JsonDocument.Parse` fails. **Do not** tolerate
empty JSON in the test reader.

**Expected after fix:** one offline confirmation at 10s → data: offline page with `data-shell-offline-page=1`
before JSON sampling (B-1 / backoff measure loops).

## Integration evidence targets (coordinator gate)

| Case | Trace |
|------|--------|
| (a) recovery | `nav-starting` → `nav-completed-panel-ok` after offline |
| (b) timeout once | single `branch=panel-nav-timeout` (not `no-pending` skip loop) |
| (c) B-1 flap | time-to-first-offline after `shell-probe-flap-baseline` |

WPF test: `shell_pending_panel_nav_past_deadline_confirms_once_late_completion_orphan_timed_out`
(`ShellPanelNavigationFailureTests`).

### Why that test fails on 05762b6 (not run on Linux agent; checked via CI trace)

On `05762b6`, `gcm-shell-afcf38fe…`: **0** confirming `branch=panel-nav-timeout` lines; **many**
`panel-nav-timeout-skip reason=no-pending`; **0** `timedOut=` on orphans; `NavigationStartingCount`
would grow ~1 per 10s during the 20s post-offline wait (offline never shown → `WaitForPanelUnavailableOfflineAsync`
timeout or loop). The new test asserts exactly **1** timeout confirm, **0** `no-pending` skips, stable nav count,
and `timedOut=` on a late orphan.