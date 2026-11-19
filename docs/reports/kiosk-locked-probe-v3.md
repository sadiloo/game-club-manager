# Kiosk locked probe v3 (logging only, diagnosis)

Goal: explain WPF stability failures where wallet top-up/credential succeed but Postgres has **zero** `sessions` rows for the station while the UI stays **Locked** with empty `hostSessionId`. No fixes in this probe.

Branches (pushed, no PR):

| Phase | Branch | Base | Head (`git rev-parse`) |
|-------|--------|------|-------------------------|
| A | `cursor/kiosk-locked-probe-v3-pr42` | `cursor/kiosk-locked-probe-v2-pr42` @ `b718edd` | `a4d8d70` (after probe fix `dbf113e` equivalent on B) |
| B | `cursor/kiosk-locked-probe-v3` | `cursor/kiosk-locked-probe-v2` @ `10f7d6c` | `dbf113e` |

## Instrumentation added (logging only)

| Layer | What |
|-------|------|
| Server | `ProbeApiRequestLoggingMiddleware` when `GAMECLUB_PROBE_HTTP_LOG=1` → `PROBE_V3_API` lines (method, path, status, UTC req/res, redacted bodies) for `/api/kiosk` and `/api/operator` |
| Server | `PROBE_V3_SESSION` on successful `LoginAndAttachAsync` (raw vs `StationIdNormalizer` station id, session id) |
| Harness | `PROBE_HTTP_TRACE` + `STAB_STEP` in `WpfLiveServerTestHelper` (fund path only; **no** direct session start API) |
| CI failure | `PROBE_V3_FAILURE_EVIDENCE` in `scripts/windows/ci-integration-wpf-stability.ps1` (API trace, server slice, step trace, `stations` + all `sessions` for raw/normalized id) |

CI order unchanged from v2 (integration → mutations → orphan → host_logout 20× → **5×20** stability = 100 runs). Branch heads above exclude any product fix.

## CI attempt 1 (aborted — probe harness, not product)

| Phase | Run | Outcome | Cause (label: **test-harness**) |
|-------|-----|---------|----------------------------------|
| A | [44632354599](https://github.com/sadiloo/game-club-manager/actions/runs/44632354599) | failed &lt;21m | net48 build: `ProbeHttpCallTrace.cs:57` expression-bodied `Clear()` with `lock`; ps1: `$Label:` parse in `Invoke-ProbePostgresQuery` |
| B | [44632351764](https://github.com/sadiloo/game-club-manager/actions/runs/44632351764) | failed &lt;21m | same; integration step never reached WPF stability |

Fix commit `fix: probe v3 net48 Clear() and ps1 Label interpolation` on both heads (above). **Re-run CI in progress** (no stability data from attempt 1).

## CI attempt 2 (in progress)

| Phase | Workflow run | Kiosk job |
|-------|----------------|-----------|
| A | _(pending push run for `a4d8d70`)_ | |
| B | _(pending push run for `dbf113e`)_ | |

### Pass/fail counts (5 batches × 20)

| Phase | Batch 1 | Batch 2 | Batch 3 | Batch 4 | Batch 5 | Host logout 20× |
|-------|---------|---------|---------|---------|---------|-----------------|
| A | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ |
| B | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ |

## Per-failure timelines

_Populated from job logs and `artifacts/kiosk-windows-logs/wpf-stability-failure-evidence-<global>.log` when each run finishes._

## Working hypothesis (v2 prior art; v3 evidence will confirm or reject)

v2 run global **25** on PR42 base: `sessions` **0 rows** for station; harness fund HTTP **200/204**; kiosk **`hostSessionId` all-zero** with `lockedReason=other:empty_host_session` (`KioskShellOrchestrator.cs` ~108). That pattern points to **product**: kiosk login/session bind race or ordering (`KioskHostService` / `KioskShellOrchestrator`), not missing harness session start — harness never calls `/api/kiosk/auth/login`; WPF kiosk does.

v3 logging will show whether **`POST /api/kiosk/auth/login`** is absent, 4xx/409, or succeeds without a persisted session (case-normalization mismatch on `StationId` is an explicit check in evidence block 4).

## STOP

Report will be completed with batch pass/fail tables and full failure timelines when Windows CI terminates on both branches. No PR opened.