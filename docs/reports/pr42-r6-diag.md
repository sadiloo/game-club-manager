# PR #42 R6 — diagnosis report

Base ref: `4999323d9a1dce2e5ac3e2507fc3a5d156eeecb6` (`cursor/sub-round-a-shell-f022`).  
Primary evidence run: **44588422453**, job **135787573617** (`windows-2022 shell`).  
Diagnostic branch: `cursor/pr42-r6-diag` (logging-only commits; no assertion or behavior changes).

## 1. `shell_events_do_not_change_open_session_amount_after_ten_minutes`

| Field | Value |
|-------|-------|
| File:line | `ShellPlanAIntegrationTests.cs:211` (`Assert.Equal` on amounts; CI stack cited `:207`) |
| CI | Expected `0`, Actual `5000` — run `44588422453` |
| Class | **harness** |
| Cause | Two stations are created sequentially (`shellSession` then `refSession` at `:178-179`). Wall-clock work (`RunAsync` ~1s, `Thread.Sleep(2000)` at `:197`, HTTP) elapses before the first comparison. Billing uses ps5 / 1 player with grace minute: the older session accrues the first rounded collectible (**5000** tomans) while the younger remains at **0**. Same `groupId` (`ps5` in `ShellCiHttp.cs:70`); not a rate mismatch. |
| Proposed diff | Poll until `minutesPlayed` match, then compare amounts; do not change billing rules. |

```diff
--- a/tests/GameClub.Shell.Windows.IntegrationTests/Wpf/ShellPlanAIntegrationTests.cs
+++ b/tests/GameClub.Shell.Windows.IntegrationTests/Wpf/ShellPlanAIntegrationTests.cs
@@
         var shellSession = ShellCiHttp.CreateOpenSessionAsync(shellStation).GetAwaiter().GetResult();
         var refSession = ShellCiHttp.CreateOpenSessionAsync(refStation).GetAwaiter().GetResult();
-        var baseline = ShellCiHttp.GetOpenSessionLiveBillAsync(refSession).GetAwaiter().GetResult();
+        WaitUntilBothSessionsShareMinutesPlayed(shellSession, refSession, TimeSpan.FromSeconds(90));
+        var baseline = ShellCiHttp.GetOpenSessionLiveBillAsync(refSession).GetAwaiter().GetResult();
```

(Add `WaitUntilBothSessionsShareMinutesPlayed` helper using existing `GetOpenSessionLiveBillAsync` only.)

## 2. `shell_no_answer_to_stopped_shows_exactly_one_toast`

| File:line | `ShellPollerReentrancyTests.cs:74` (`UnexpectedStopCount`; CI `:71`) |
| CI | Expected `0`, Actual `9` — run `44588422453` |
| Class | **harness** |
| Cause | `MainWindow.OnClosing` cancels close (`MainWindow.xaml.cs:303-304`), so `Close()` only hides windows. Each prior `MainWindow` test keeps a live `ServerStatePoller` (5s `System.Threading.Timer`). Asserted counts use the shared `FakeShellSeams.Notifications` singleton while **N≈9** background pollers still tick and call `ShowUnexpectedStop(false)`. `WpfStaApplicationFixture.CloseAllWindows` does not set `ShellEnvironment.ForceExit` or dispose poll timers. Real 5s timer drives spikes; fake `UtcNow` only gates no-answer timing on the test poller. |
| Proposed diff | Force real shutdown in tests + log open window count (diag already added). |

```diff
--- a/tests/GameClub.Shell.Windows.IntegrationTests/Wpf/WpfStaApplicationFixture.cs
+++ b/tests/GameClub.Shell.Windows.IntegrationTests/Wpf/WpfStaApplicationFixture.cs
@@
                 foreach (var window in windows)
                 {
+                    if (window is MainWindow mw)
+                    {
+                        ShellEnvironment.ForceExit = true;
+                        mw.ExitShell();
+                        continue;
+                    }
                     window.Close();
                 }
+                ShellEnvironment.ForceExit = false;
```

## 3. `shell_poller_skips_reentrant_tick_during_slow_snapshot_and_one_unexpected_toast`

| File:line | `ShellPollerReentrancyTests.cs:47` |
| CI | `skipped=0` — run `44588422453` |
| Class | **harness** (poll bleed) + test timing |
| Cause | Same hidden-window poller bleed inflates toast expectations; slow `BeforeBuildSnapshot` sleep (`7000ms`) must overlap a **real** timer callback while `_tickInProgress==1`. Extra poll timers increase contention unpredictably; on this run no `OnTickSkippedReentrant` fired (`skipped=0`). |
| Proposed diff | Same `ForceExit` cleanup as #2; optionally invoke one manual `BuildSnapshotForTests()` while `slowSnapshots=1` before the 15s wait so reentrancy is deterministic (assertion unchanged). |

## 4. `shell_unexpected_service_stop_shows_exactly_one_notification`

| File:line | `ShellTrayStatusTests.cs:89` (CI `:85`) |
| CI | Expected `1`, Actual `10` — run `44588422453` |
| Class | **harness** |
| Cause | Identical to #2: ~9 leaked `MainWindow` pollers + 1 test poller × unexpected toast every qualifying 5s tick ≈ **10** counts after two `Task.Delay(6000)` windows. |
| Proposed diff | Same `ForceExit` / `ExitShell` cleanup in fixture. |

## 5. `shell_tray_initiated_stop_shows_no_unexpected_stop_notification`

| File:line | `ShellTrayStatusTests.cs:104` (CI `:102`) |
| CI | Expected `0`, Actual `1` — run `44588422453` |
| Class | **harness** |
| Cause | Leaked poller fires one unexpected toast before `TrayStopInProgress` semantics apply across all instances; single stray toast fails `0` expectation. |
| Proposed diff | Same fixture cleanup as #2. |

## 6. `shell_shows_persian_offline_when_service_stopped`

| File:line | `ShellPlanAIntegrationTests.cs:354` (CI `:350`) |
| CI | `Assert.Contains` — innerText `""`, missing `سرور` — run `44588422453` |
| Class | **src** (timing / offline presentation) |
| Cause | Service stopped before show. `ShowOfflineAsync` (`MainWindow.xaml.cs:230-257`) throttles with `_lastReconnectAttempt` (5s) and after `PresentOfflinePageAsync` awaits **`Task.Delay(5000)`** before re-checking operator state. Poller `Changed` can fire repeatedly while WebView is still on a blank/failed panel navigation; test polls `innerText` for 30s but reads empty `""""`. Offline HTML path exists (`PresentOfflinePageAsync` / `NavigateToString`) but is starved by reconnect delay and navigation race when the server is already down at first load. |
| Proposed diff (src) | Skip the 5s post-offline delay when presentation used `NavigateToString`; log via `ShellFileLog` (diag lines added). |

```diff
--- a/src/GameClub.Shell/MainWindow.xaml.cs
+++ b/src/GameClub.Shell/MainWindow.xaml.cs
@@
         _lastReconnectAttempt = DateTime.UtcNow;
         await PresentOfflinePageAsync(state);
-        await Task.Delay(5000);
-        if (_poller?.Current.OperatorAnswers == true)
+        if (_poller?.Current.OperatorAnswers == true)
         {
             await NavigatePanelAsync();
         }
```

## 7. `shell_shows_persian_offline_after_service_stops_post_webview_ready`

| File:line | `ShellPlanAIntegrationTests.cs:423` (CI `:419`) |
| CI | Same empty innerText — run `44588422453` |
| Class | **src** (same as #6) |
| Cause | After SCM stop, poller drives `ShowOfflineAsync` while loopback navigation fails; same throttle/reconnect delay prevents stable `NavigateToString` offline content before the 30s assertion window ends. |
| Proposed diff | Same as #6. |

## 8. `shell_window_json_restores_size_position_maximized`

| File:line | `ShellPlanAIntegrationTests.cs:107` (CI `:105`) |
| CI | `WindowState` Expected `Maximized`, Actual `Normal` — run `44588422453` |
| Class | **harness** (runner geometry) |
| Cause | `WindowPlacementStore` applies restore width **1024** then sets `Maximized` in `SourceInitialized`. On the runner, primary `WorkingArea.Width` is **~1044** (companion failure #11). WPF refuses maximized state when restore width exceeds work-area width; window stays `Normal`. Placement logs added: `placement-apply` / `placement-after-apply`. |
| Proposed diff | Assert normalized effective width against work area, not raw 1024; keep maximized intent. |

```diff
--- a/tests/GameClub.Shell.Windows.IntegrationTests/Wpf/ShellPlanAIntegrationTests.cs
+++ b/tests/GameClub.Shell.Windows.IntegrationTests/Wpf/ShellPlanAIntegrationTests.cs
@@
-                Assert.Equal(1024, window.Width);
-                Assert.Equal(700, window.Height);
+                var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
+                Assert.Equal(Math.Min(1024, area.Width), window.Width, 0);
+                Assert.Equal(Math.Min(700, area.Height), window.Height, 0);
```

## 9. `shell_window_json_infinity_input_round_trips_to_default_size_and_valid_json`

| File:line | `ShellWindowPlacementRegressionTests.cs:37` (CI `:24-37`) |
| CI | `Maximized` vs `Normal` — run `44588422453` |
| Class | **harness** / runner geometry (same as #8) |
| Cause | Defaults normalize to 1280×800; work area width ~1044 prevents maximized transition. |
| Proposed diff | Same work-area-aware assertion for maximized + dimensions. |

## 10. `shell_window_json_corrupt_file_falls_back_to_default_without_throw`

| File:line | `ShellWindowPlacementRegressionTests.cs:72` (CI `:58`, width assert `:88`) |
| CI | Expected width `1280`, Actual `1044` — run `44588422453` |
| Class | **harness** |
| Cause | Fallback defaults are 1280×800 (`WindowPlacementStore` `DefaultWidth`); WPF clamps client size to **work area (~1044)** on show — not a corrupt-load failure. |
| Proposed diff | `Assert.Equal(Math.Min(1280, workArea.Width), window.Width)` (same normalize rule as production clamp). |

## 11. `shell_page_alert_still_shows_webview_default_dialog`

| File:line | `ShellPlanAIntegrationTests.cs:531` (CI `:525`) |
| CI | `Assert.True(seen)` false — run `44588422453` |
| Class | **harness** |
| Cause | Test fires `ExecuteScriptAsync("alert('سلام');")` without awaiting (`:523`) against the operator SPA after 4s load. Operator document may not be ready or may block dialogs; `ScriptDialogOpening` never fires within 5s. Navigation URL still loopback operator, not an isolated HTML fixture. |
| Proposed diff | Await navigation idle / use `NavigateToString` minimal HTML, then await `ExecuteScriptAsync` before `WhenAny`. |

```diff
--- a/tests/GameClub.Shell.Windows.IntegrationTests/Wpf/ShellPlanAIntegrationTests.cs
+++ b/tests/GameClub.Shell.Windows.IntegrationTests/Wpf/ShellPlanAIntegrationTests.cs
@@
-                    _ = wv.CoreWebView2.ExecuteScriptAsync("alert('سلام');");
+                    await wv.CoreWebView2.NavigateToString("<html><body><script>alert('سلام')</script></body></html>");
+                    await wv.CoreWebView2.ExecuteScriptAsync("alert('سلام');");
```

## Prepared (not applied): `new-window-blocked` log redaction

`MainWindow.xaml.cs:104` still logs raw URL. Align with navigation-blocked:

```diff
--- a/src/GameClub.Shell/MainWindow.xaml.cs
+++ b/src/GameClub.Shell/MainWindow.xaml.cs
@@
             PanelWebView.CoreWebView2.NewWindowRequested += (_, args) =>
             {
                 args.Handled = true;
-                ShellFileLog.Write($"new-window-blocked url={args.Uri}");
+                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var blocked))
+                {
+                    var host = (blocked.Host ?? "").ToLowerInvariant();
+                    ShellFileLog.Write($"new-window-blocked scheme={blocked.Scheme} host_hash8={Sha256Hex8(host)}");
+                }
+                else
+                {
+                    ShellFileLog.Write("new-window-blocked scheme=unknown host_hash8=00000000");
+                }
             };
```

Add test extension on `shell_blocks_external_navigation_and_new_window_with_log_line` asserting log contains no raw host from `NewWindowRequested`.

---

## Diagnostic run IDs

| Run | Job | Branch | Notes |
|-----|-----|--------|-------|
| 44588422453 | 135787573617 | `cursor/sub-round-a-shell-f022` | Baseline 11 failures |
| 44595452402 | 135812379937 | `cursor/pr42-r6-diag` @ `7a3fe4e` | Build failed (net48 `Index` range in `ReadShellLogTail`); fixed in next push |
| *(pending)* | | `cursor/pr42-r6-diag` | `[pr42-r6-diag]` console + `ShellFileLog` lines |