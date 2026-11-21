# PR #42 shell R9 report (staging)

Parent SHA: `11f86d725399549e0a323fe208aaed322a456b04` (`cursor/pr42-r8-staging`).

## Branch head

| SHA | Message |
|-----|---------|
| `e92d9a520c470b012f4da31d7ee8c3f1657251ca` | PR42 R9: money floor, SHELL-GEOM/OFFLINE, race diagnostics |
| `7cebb915ced4e744e72c5858e0ac04a8baf3c4ac` | ci: trigger shell job for e92d9a5 report (empty; same tree as `e92d9a5`) |

## CI (latest substantive + retry)

- **Run id:** [44636813862](https://github.com/sadiloo/game-club-manager/actions/runs/44636813862) @ `7cebb91`
- **Run id (prior):** [44632219765](https://github.com/sadiloo/game-club-manager/actions/runs/44632219765) @ `e92d9a5` — shell job failed on Chocolatey `postgresql16` 503

| Job | Result @ `7cebb91` |
|-----|---------------------|
| windows net48 and net10 | **success** |
| windows-2022 shell | **failure** — `install and start postgresql` (Chocolatey 503; shell tests **not run**) |
| windows-2022 kiosk integration | **failure** — postgres install + downstream |
| ubuntu net10 | success (when workflow completed) |

**Reference shell integration (last full run):** [44627575363](https://github.com/sadiloo/game-club-manager/actions/runs/44627575363) @ `0257ede` — **38 passed / 9 failed / 47** (pre–billing-wait restore).

### Compile gate (local)

Release `-p:EnableWindowsTargeting=true`: Server, Shell, Shell.Windows.IntegrationTests, Server.Tests — **0 errors**.

## Test-only changes

1. **Billing wait restored** (`7339f1a`); **floors** (`e92d9a5`): `elapsedWholeMin >= waitSeconds/60` and `timeGrossToman > 2667` on both REF and A3 before segment asserts.
2. **SHELL-RACE** timeline in `ShellPollerCloseRaceTests.cs` (UTC, thread, dispatcher, close/gate/poller flags, counters pre-assert, `Assert.Equal(0, …)` via `AssertZero` + distinct messages).
3. **SHELL-GEOM** phases (PreShow, SourceInitialized-after-ApplyBounds, Loaded, Show) in placement tests.
4. **SHELL-OFFLINE** polls on post-stop offline test.

### Money test (expected on next green shell job)

With `SHELL_A3_BILLING_WAIT_SECONDS=600`, frozen bills should show **`elapsedWholeMin >= 10`** and **`timeGrossToman > 2667`** or the test throws with full timeline (no minimum-charge pass).

**Last SHELL-MONEY lines (run 44627575363, no billing wait — would fail new floors):**

```
SHELL-MONEY session=REF ... elapsedWholeMin=0 ... timeGrossToman=2667
SHELL-MONEY session=A3 ... elapsedWholeMin=0 ... timeGrossToman=2667
```

### Race test

Diagnostics land on run **> `e92d9a5`**. Reference failure (44627575363, old test): `ShellPollerCloseRaceTests` @ line 97 (`updateSnapshotAfterClose` assert). **No SHELL-RACE lines** on that head (pre-diagnostic).

### Geometry (diagnose only)

| Item | Location |
|------|----------|
| `ApplyBounds()` body | `WindowPlacementStore.cs:28-37` |
| Called from `OnSourceInitialized` | `WindowPlacementStore.cs:44-47` |
| Wired in `Apply` | `WindowPlacementStore.cs:41-42` (`SourceInitialized` + `Loaded`) |
| `WindowPlacementStore.Apply(this)` | `MainWindow.xaml.cs:36` |
| `WindowState.Maximized` | `WindowPlacementStore.cs:53-55` in `OnLoadedOnce` |
| Work-area clamp | `WindowPlacementStore.cs:121-125` `ClampDimensionsToWorkingArea` |
| XAML min size (no fixed Width) | `MainWindow.xaml:7-8` `MinWidth=480` `MinHeight=640` |
| Maximized assert at Show | `ShellWindowPlacementRegressionTests.cs:49` (`ShellPlanAIntegrationTests.cs:119` placement-restore) — runs **synchronously after `Show()`**, **before** `Loaded` fires (Loaded is event-driven; Maximized is applied in `OnLoadedOnce` at `WindowPlacementStore.cs:50-56`) |

**Run 44627575363 SHELL-GEOMETRY (no SHELL-GEOM yet):** Loaded `width=1024`; Show `width=1040` with `state=Maximized` — width at Show reflects maximized layout metrics, not `Math.Min(1024, workArea)` on Normal bounds from `ApplyBounds`.

### Offline (diagnose only)

| Item | Location |
|------|----------|
| Poller → offline when not answering | `MainWindow.xaml.cs:81-84` → `ShowOfflineAsync` |
| Present offline HTML | `MainWindow.xaml.cs:312-325` `PresentOfflinePageAsync` |
| Fallback on failed panel nav | `MainWindow.xaml.cs:161-178` `OnNavigationCompleted` |

**Run 44627575363 SHELL-OFFLINE (`shell_offline_page_clears_when_service_returns_to_running`):** `service=Stopped`, `webViewReady=True`, **`uri=http://127.0.0.1:5000/operator/`** for entire poll window — offline `data:` page never shown; test failed. Likely: panel already on operator URL; `ShowOfflineAsync` / `PresentOfflinePageAsync` not winning over existing navigation (timing or throttle at `MainWindow.xaml.cs:303-306`). **No `src/` change** (per brief).

## Open questions

- Re-run Windows shell job when Chocolatey postgres feed is healthy to capture SHELL-MONEY / SHELL-GEOM / SHELL-RACE on `7cebb91`.