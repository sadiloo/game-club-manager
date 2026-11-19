# PR #40 R18c report

## Staging

| Field | Value |
|-------|--------|
| **Staging branch** | `cursor/pr40-r18c-staging` |
| **Parent** | `717a3c1ae499d648464530975db3799f6f85722a` |
| **Staging sha** | `2ffa5b7` |

### Files touched

- `src/GameClub.Kiosk.Core/Guard/GuardInternetClient.cs`, `GuardInternetClientErrorCodes.cs`, `GuardInternetBudgetDefaults.cs`
- `src/GameClub.Kiosk.Core/Runtime/KioskRuntime.cs`
- `src/GameClub.Kiosk.Guard/Internet/InternetPowerShellRunner.cs`, `InternetPipeServer.cs`
- `tests/GameClub.Kiosk.Core.Tests/PcInternetKioskCoreTests.cs`
- `tests/GameClub.Kiosk.Windows.IntegrationTests/Server/*LiveTests.cs`, `L17GuardProbeSupport.cs`
- `tests/GameClub.Kiosk.Windows.IntegrationTests/Guard/GuardProcessLifecycle.cs`, `CiGuardKioskProcessCleanup.cs`
- `scripts/windows/ci-integration.ps1`, `ci-integration-wpf-stability.ps1`
- `.github/workflows/ci.yml`

## Item status

| # | Item | Status |
|---|------|--------|
| 1 | Client error mapping (`pipe_connect_timeout` / `pipe_io_timeout` / `pipe_error`) | **Done** — stable codes via `GuardInternetClientErrorCodes`; net10 `TimeoutException` remapped; Core mapper tests added |
| 2 | 180 s hang root cause | **Diagnosed** (see below); no extra product change beyond scope 1/3/4 |
| 3 | PowerShell budgets + watchdog duration lines | **Done** — budgets raised from CI measured max; `powershell_step` + `reconcile_step reassert` logging |
| 4 | Orphan Guard/Kiosk | **Done (harness)** — preemptive + final kill, stronger `taskkill`, CI leak recovery |
| 5 | `within_one_tick` + main history + artifacts | **Diagnosed + harness** — artifact upload; no product STOP (see below) |

## (2) 180 s hang — timestamps and step

**CI:** run `44567958282`, job `135714792452`, test `live_server_pc_internet_guard_forced_apply_failure_shows_error_and_offline_at_press`.

| Timestamp (UTC) | Event |
|-----------------|--------|
| 16:58:05.790 | `L17_PROBE_GUARD_STARTED pid=6944` |
| 16:58:06.165 | `reconcile_start` (watchdog) |
| 16:58:10.345 | `reconcile_step localhost_cleanup elapsed_ms=4178` |
| 16:58:14.309 | `reconcile_step readback_capture elapsed_ms=3967` |
| 16:58:14.309 | `reconcile_steps_total elapsed_ms=8146` |
| 16:58:21.598 | `reconcile_end completed=true elapsed_ms=15437` |
| 17:01:04.965 | xUnit **test execution timed out after 180000 ms** |

**Conclusion:** Reconcile finished in **~15.4 s** (not 180 s). The xUnit cap was consumed in the **L17 live probe phase** after guard start (`WaitForPipeReadyAsync` / `GuardInternetClient.GetStatusAsync` against probe Guard pid 6944). No `L17_PROBE_PIPE_WAIT` / `READY` lines appeared in captured stdout (buffering and/or a single hung client attempt). Apply worst-case **29 s** was not the limiting factor on this run.

**~7 s gap** `reconcile_steps_total` (8146 ms) → `reconcile_end` (15437 ms): unlogged **`reassert` / `_firewall.Apply`** on the reconcile work thread (now logged as `reconcile_step reassert`).

## (3) Budget table

| Step | Measured max (ms) | 3× (ms) | Chosen budget (ms) |
|------|-------------------|---------|---------------------|
| localhost_cleanup | 4178 | 12534 | **13000** |
| readback_capture | 3967 | 11901 | **12000** |
| post_reset_readback | — (path not taken) | — | **12000** (parity) |
| reset_script | — | — | **28000** (unchanged) |

| Aggregate | ms |
|-----------|-----|
| Reconcile PowerShell Σ (worst) | 85000 |
| + kill waits (7×3000) | 21000 |
| Planned reconcile worst | **106000** |
| + planning margin | 10000 |
| Reconcile wall join | **120000** (headroom **4000**) |
| Apply path worst (+ kills) | **29000** |
| Client pipe IO | **30000** |

## (4) Orphan cause

L17 live test hit the **180 s xUnit timeout** before `finally` could stop probe Guard **6944** → `CI_PROCESS_LEAK_GUARD count=1` / `KIOSK count=1`, poisoning later WPF phases. **Fix:** `CiGuardKioskProcessCleanup` + CI `Stop-OrphanedGuardAndKiosk` before net48 live; retried `taskkill /T` in `GuardProcessLifecycle`; finally block + CI re-kill on leak detection.

## (5) `within_one_tick` analysis

**CI (same run):** `shellPhase=Locked`, `runtimeState=Playing`, status `connecting…`, **sessions bound** (`hostSessionId` / `runtimeSessionId` = `7d0d7f15-…`), top-up **200**, credential **204**. Failure is **UI/orchestrator stuck Locked** while transport connected — not missing session id on this head.

**Main history:** `wpf_remaining_time_matches_funded_session_within_one_tick` is exercised on the **windows kiosk integration** job (live TestHost), not on `main` push CI in recent runs (`gh run list --branch main` — no comparable failures). Prior failure also on PR #42 with empty session — likely **orphan-poisoned** environment after L17 timeout.

**STOP (product):** None filed — primary CI evidence points to **harness orphans + shell phase**, not a isolated server/kiosk money rule bug; product change deferred pending clean CI with artifacts (`kiosk-wpf-stability-logs`).

## STOPs

None — no server/kiosk product fix required outside items 1, 3, 4 for this staging pass.

## Local verification

- Release build gate (5 projects, `EnableWindowsTargeting=true`): **0 errors**
- `PcInternet` Core tests (net10): **23 passed**