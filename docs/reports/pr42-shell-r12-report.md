# PR42 R12 — GEOM fix (applied)

## R11 failure (CI run 44650203381)

At **Loaded**, `RestoreBounds.Height` was **640** (`MainWindow.xaml:8` `MinHeight`) instead of saved **700**, and infinity round-trip saw **Normal** instead of **Maximized** at the Loaded assert.

## Cause

`OnLoadedOnce` (`WindowPlacementStore.cs:50-56`) set `WindowState = Maximized` without re-applying clamped JSON bounds. WPF captured `RestoreBounds` from the current layout (often still `MinHeight` 640) even though `OnSourceInitialized` had already called `ApplyBounds` once. Handler order and layout timing on Loaded left maximize reading the wrong restore size.

R6 **deliberately** keeps maximize on **Loaded** (not `SourceInitialized`); moving maximize earlier risks regressing the R6 red scenario.

## Fix (in `WindowPlacementStore.cs`)

In `OnLoadedOnce`, when `placement.IsMaximized`, call **`ApplyBounds()`** (same clamp as `SourceInitialized`) **immediately before** `WindowState = Maximized`, so restore bounds reflect saved/normalized width×height, then maximize in Loaded as before.

```diff
         void OnLoadedOnce(object? sender, EventArgs e)
         {
             window.Loaded -= OnLoadedOnce;
             if (placement.IsMaximized)
             {
+                ApplyBounds();
                 window.WindowState = WindowState.Maximized;
             }
         }
```

## Expected test outcomes (unchanged assertions)

| Test | Expectation |
|------|-------------|
| R6 maximized test | Stays green |
| `shell_window_json_restores_size_position_maximized` | `Maximized`, `RestoreBounds` 1024×700 (clamped) |
| `shell_window_json_infinity_input_round_trips_*` | `Maximized`, `RestoreBounds` = `Normalize` defaults after clamp (1280×800 capped to work area) |

## Tray (same R12 round)

- `TrayStopObserved`: no clear on first Running/Green tick while stop is still in flight; clear only after a non-Running/Green snapshot, then StartPending or Running/Green.
- Test (i): flag set while Running, ≥1 Running tick, then Stopped → 0 toasts.
- Test (iii): `ShellSeams.StartElevatedProcess` seam; `LaunchServerControlForTests("stop")` with `Win32Exception(1223)` clears flag; unexpected stop → 1 toast.