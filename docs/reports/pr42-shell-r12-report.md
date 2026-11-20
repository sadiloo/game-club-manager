# PR42 R12 — GEOM proposal (no src change)

## R11 SHELL-GEOM evidence (CI run 44650203381)

### placement-restore (`shell_window_json_restores_size_position_maximized`)

| Phase | state | W×H | note |
|-------|-------|-----|------|
| Loaded | Maximized | 1024×**640** | `RestoreBounds.Height` assert expects **700** |
| SourceInitialized | Maximized | 1024×**700** | after `WindowPlacementStore` `ApplyBounds` |
| Show | Maximized | 1040×744 | frame |

### infinity-round-trip (`shell_window_json_infinity_input_round_trips_*`)

| Phase | state | W×H |
|-------|-------|-----|
| Loaded | **Normal** | 1024×640 |
| SourceInitialized | Normal | 1024×728 |
| Assert | **Expected Maximized, Actual Normal** | |

## Cause

1. **640 = `MinHeight`** — `MainWindow.xaml:8` sets `MinHeight="640"`. Before saved placement is applied, the WPF window uses default layout height clamped to at least `MinHeight` (640).

2. **Maximize captures `RestoreBounds` too early** — `WindowPlacementStore.Apply` (`WindowPlacementStore.cs:50-56`) sets `WindowState = Maximized` on the **Loaded** event. If that runs while `Height` is still 640 (default/min) rather than the normalized JSON height (700), WPF records `RestoreBounds.Height = 640` for the maximized window. R11 traces show **Loaded** at 640 and **SourceInitialized-after-ApplyBounds** at 700, so the loaded assert (which runs on Loaded) observes 640.

3. **Infinity + maximize** — `Normalize` (`WindowPlacementStore.cs:98-108`) replaces non-finite width/height with defaults (1280×800) but still honors `IsMaximized: true`. When maximize is applied on Loaded while layout height is still the min clamp (640) and/or `Show` has not completed the maximize path, `WindowState` can remain **Normal** at the Loaded assert (R11: Expected Maximized, Actual Normal).

## Minimal proposed diff (not applied in R12)

Defer `WindowState = Maximized` until after `ApplyBounds` has set width/height from JSON (e.g. only in `OnSourceInitialized` after `ApplyBounds`, or on Loaded **after** verifying `Height` matches saved placement). Optionally apply maximize from `ContentRendered` when `placement.IsMaximized` and bounds match normalize output.

```diff
--- a/src/GameClub.Shell/Shell/WindowPlacementStore.cs
+++ b/src/GameClub.Shell/Shell/WindowPlacementStore.cs
@@ -44,15 +44,18 @@ internal static class WindowPlacementStore
         void OnSourceInitialized(object? sender, EventArgs e)
         {
             window.SourceInitialized -= OnSourceInitialized;
             ApplyBounds();
+            if (placement.IsMaximized)
+            {
+                window.WindowState = WindowState.Maximized;
+            }
         }
 
         void OnLoadedOnce(object? sender, EventArgs e)
         {
             window.Loaded -= OnLoadedOnce;
-            if (placement.IsMaximized)
-            {
-                window.WindowState = WindowState.Maximized;
-            }
         }
```

## Tray test isolation note (R11 failure)

`shell_tray_initiated_stop_shows_no_unexpected_stop_notification` set `TrayStopInProgress = true` **before** `WpfStaApplicationFixture.RunAsync`, which calls `FakeShellSeams.ResetHarnessStatics()` and cleared the flag before the poller ran — producing an unexpected toast. R12 moves the flag set **inside** the test body (after reset) and adds `SHELL-TRAY-TRACE` logging for flag writes and poller instance id on each tick.