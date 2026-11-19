# PR #42 shell R9 report (staging)

Parent SHA: `11f86d725399549e0a323fe208aaed322a456b04` (`cursor/pr42-r8-staging`).

## ls-remote

```
0257ede3fcf65615253bdfd62a6d287a51f8d38a	refs/heads/cursor/pr42-r9-staging
```

## CI run

- **Run id:** [44627575363](https://github.com/sadiloo/game-club-manager/actions/runs/44627575363)
- **Trigger:** push `cursor/pr42-r9-staging` @ `0257ede`

### Per-job results (at shell job completion)

| Job | Result |
|-----|--------|
| windows net48 and net10 | success |
| **windows-2022 shell** | **failure** — 38 passed / 9 failed / 47 total (~4.65 min) |
| windows-2022 kiosk integration | in progress |
| ubuntu net10 | in progress |

### Compile gate (local, this agent)

Release `-p:EnableWindowsTargeting=true`: `GameClub.Server`, `GameClub.Shell`, `GameClub.Shell.Windows.IntegrationTests`, `GameClub.Server.Tests` — **0 errors**.

## Test-only changes (brief items 1–2)

### 1. Money test rewrite

**Clock strategy:** runner clock step 3 (`WaitUntilBothSessionsClearMinuteBoundary` in `ShellCiHttp.cs`); no controllable server/TestHost clock in shell CI.

**Read-only / billing endpoints used:**

- `GET /api/operator/grid` — assert both stations `status=Playing` after shell events
- `GET /api/operator/sessions/{id}/close-settle-preview` — open-session probe; segment start anchor at create (server `Date` header on first preview)
- `POST /api/operator/sessions/{id}/bill/open` then `GET .../bill` — frozen `segments[].startUtc`, `frozenAtUtc`, amounts (no early `bill/open` during shell activity)

**Files:** `ShellCiHttp.cs`, `ShellPlanAIntegrationTests.cs` (`shell_events_do_not_change_open_session_amount_after_ten_minutes`).

**CI:** `shell_events_do_not_change_open_session_amount_after_ten_minutes` — **PASSED** (15 s).

**SHELL-MONEY lines (run 44627575363):**

```
SHELL-MONEY minute-boundary-clear UTC=2026-11-19T22:34:01.0187725Z refStart=2026-11-19T22:33:46.0000000Z shellStart=2026-11-19T22:33:46.0000000Z
SHELL-MONEY session=REF startUtc=2026-11-19T22:33:46.7355640Z frozenAtUtc=2026-11-19T22:34:01.1478680Z elapsedWholeMin=0 collectibleToman=5000 timeGrossToman=2667
SHELL-MONEY session=A3 startUtc=2026-11-19T22:33:46.1577310Z frozenAtUtc=2026-11-19T22:34:01.4053910Z elapsedWholeMin=0 collectibleToman=5000 timeGrossToman=2667
```

Amounts match after freeze (no product change attempted).

### 2. Diagnose-only logging (geometry / offline)

**Files:** `ShellPlanAIntegrationTests.cs`, `ShellWindowPlacementRegressionTests.cs` — `SHELL-GEOMETRY` and `SHELL-OFFLINE` `Console.WriteLine` only; assertions unchanged.

#### Geometry

**`shell_window_json_restores_size_position_maximized`** — FAILED @ `ShellPlanAIntegrationTests.cs:115` (width assert).

```
Expected: 1024  Actual: 1040
```

**SHELL-GEOMETRY timeline:**

```
phase=Loaded         state=Maximized width=1024 height=640
phase=Show           state=Maximized width=1040 height=744
phase=ContentRendered state=Maximized width=1040 height=744
```

**Hypothesis:** `WindowPlacementStore` applies maximized bounds on `Loaded`/`SourceInitialized`, but the test asserts width at `Show()` (`:115`) while `window.Width` is still 1040 (pre–work-area clamp). Same timing class as R8 (`WindowPlacementStore.cs:44-56`).

**`shell_window_json_infinity_input_round_trips_to_default_size_and_valid_json`** — FAILED @ `ShellWindowPlacementRegressionTests.cs:43` (`Maximized` vs `Normal`).

**SHELL-GEOMETRY timeline:**

```
phase=Show            state=Normal width=1024 height=728
phase=Loaded          state=Normal width=1024 height=640
phase=ContentRendered state=Normal width=1024 height=728
```

**Hypothesis:** `IsMaximized: true` is applied in `Loaded` (`WindowPlacementStore.cs:50-56`); assert at `Show()` (`:43`) runs before `WindowState` becomes `Maximized`.

#### Offline

**`shell_offline_page_clears_when_service_returns_to_running`** — FAILED @ `ShellPlanAIntegrationTests.cs:599` (`offlineSeen`).

**SHELL-OFFLINE sample polls (service stopped, WebView ready):**

```
service=Stopped webViewReady=True uri=http://127.0.0.1:5000/operator/
(repeats for ~45s; no navigation away from /operator/)
```

**Hypothesis:** With `CheckpointServer` stopped, poller/health path does not flip the shell to embedded offline HTML before the assert; WebView remains on operator URL (`MainWindow.xaml.cs:254-262` / `PresentOfflinePageAsync` vs prior panel load). No `panel-navigate` lines in Actions stdout (file log only). Assertions unchanged.

## Files changed (vs parent)

- `tests/GameClub.Shell.Windows.IntegrationTests/Shell/ShellCiHttp.cs`
- `tests/GameClub.Shell.Windows.IntegrationTests/Wpf/ShellPlanAIntegrationTests.cs`
- `tests/GameClub.Shell.Windows.IntegrationTests/Wpf/ShellWindowPlacementRegressionTests.cs`
- `docs/reports/pr42-shell-r9-report.md`

## Open questions

None for R9 test-only scope. Remaining shell failures are pre-existing (offline, poller race, tray stop, page_alert) — not addressed per brief.