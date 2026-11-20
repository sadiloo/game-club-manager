# Kiosk locked probe v3 (logging only, diagnosis)

Goal: explain WPF stability failures where wallet top-up/credential succeed but Postgres has **zero** `sessions` rows for the station while the UI stays **Locked** with empty `hostSessionId`. No fixes in this probe.

Branches (pushed, no PR):

| Phase | Branch | Base | Head (`git rev-parse`) |
|-------|--------|------|-------------------------|
| A | `cursor/kiosk-locked-probe-v3-pr42` | `cursor/kiosk-locked-probe-v2-pr42` @ `b718edd` | `e203184` |
| B | `cursor/kiosk-locked-probe-v3` | `cursor/kiosk-locked-probe-v2` @ `10f7d6c` | `b2a1ccb` |

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

Fix commit `fix: probe v3 net48 Clear() and ps1 Label interpolation` on both heads. **No stability data from attempt 1.**

## CI attempt 2 (aborted — probe harness)

| Phase | Run | Stability batches (fail/total) | Cause (**test-harness**) |
|-------|-----|-------------------------------|--------------------------|
| B | [44635656555](https://github.com/sadiloo/game-club-manager/actions/runs/44635656555) | **20/20 each batch, 100/100 total** (instant exit=1) | net48 build failed: `ProbeHttpCallTrace.cs` used C# range/index `..` (needs `Substring` on net48); no `IntegrationTests.dll` → all vstest runs missing assembly |
| A | [44635658738](https://github.com/sadiloo/game-club-manager/actions/runs/44635658738) | same pattern | same Range/`Index` issue |

Fix: `3218131` (B) / `a6347a0` (A) — net48-safe truncation in `ProbeHttpCallTrace.cs`.

## CI attempt 3 (aborted — CI infra + harness; **no WPF stability polls**)

| Phase | Head | Run | Job outcome |
|-------|------|-----|-------------|
| A | `45b5825` | [44637788243](https://github.com/sadiloo/game-club-manager/actions/runs/44637788243) | `windows-2022 kiosk integration` **failure**; `windows-2022 shell` **failure** |
| B | `7c880a7` | [44637788933](https://github.com/sadiloo/game-club-manager/actions/runs/44637788933) | same pattern |

### Pass/fail counts (5 batches × 20)

| Phase | Batch 1 | Batch 2 | Batch 3 | Batch 4 | Batch 5 | Host logout 20× | `windows kiosk integration` |
|-------|---------|---------|---------|---------|---------|-----------------|---------------------------|
| A | 0/20* | 0/20* | 0/20* | 0/20* | 0/20* | **20/20 pass** | **skipped** (postgres step failed) |
| B | 0/20* | 0/20* | 0/20* | 0/20* | 0/20* | _(same run family)_ | **skipped** |

\*Each batch exited in ~12–25s at `ci-integration-wpf-stability.ps1:51` — **never reached** `WindowsKioskWpfSmokeTests` (~90s polls). `WPF_STABILITY_RUNS: 0` in workflow env at end.

### Root cause (attempt 3)

| Step | Label | Detail |
|------|-------|--------|
| `install and start postgresql` (~92s) | **CI infra** | Step failed (`postgresql service not ready` or Chocolatey/install never left Postgres listening on `127.0.0.1:5432`). Both integration and shell jobs failed here. |
| `probe v3 wpf stability batch *` | **test-harness / workflow** | Workflow still runs stability batches after postgres failure (`always()`-style ordering). `LiveDatabaseResetHarnessTests.harness_live_database_reset_only` → `Npgsql` **connection refused** → throw at `ci-integration-wpf-stability.ps1:51` (`LiveDatabaseMigration.cs:17`, `LiveDatabaseResetHarnessTests.cs:13`). |
| Mutations / orphan / host_logout | n/a | Steps **passed** on integration job despite postgres failure (they do not require live Postgres for this probe path). |

**No** `wpf-stability-failure-evidence-*.log` with `PROBE_V3_FAILURE_EVIDENCE` sections — zero stability test iterations executed. v3 product instrumentation was **not exercised** on attempt 3.

### Per-failure timelines

_Not applicable for attempt 3 (no stability failures, only pre-test DB reset failures)._

## Working hypothesis (v2 prior art; v3 evidence will confirm or reject)

v2 run global **25** on PR42 base: `sessions` **0 rows** for station; harness fund HTTP **200/204**; kiosk **`hostSessionId` all-zero** with `lockedReason=other:empty_host_session` (`KioskShellOrchestrator.cs` ~108). That pattern points to **product**: kiosk login/session bind race or ordering (`KioskHostService` / `KioskShellOrchestrator`), not missing harness session start — harness never calls `/api/kiosk/auth/login`; WPF kiosk does.

v3 logging will show whether **`POST /api/kiosk/auth/login`** is absent, 4xx/409, or succeeds without a persisted session (case-normalization mismatch on `StationId` is an explicit check in evidence block 4) when stability **fails** and `PROBE_V3_FAILURE_EVIDENCE` is emitted.

## CI attempt 4 (Postgres healthy; full stability)

| Phase | Head | Run | Outcome |
|-------|------|-----|---------|
| B | `b2a1ccb` | [44639255965](https://github.com/sadiloo/game-club-manager/actions/runs/44639255965) | **success** (~37m integration job) |
| A | `e203184` | [44639252221](https://github.com/sadiloo/game-club-manager/actions/runs/44639252221) | _in progress at last update_ |

### Pass/fail counts (5 batches × 20) — attempt 4

| Phase | Batch 1 | Batch 2 | Batch 3 | Batch 4 | Batch 5 | Total | Failure evidence |
|-------|---------|---------|---------|---------|---------|-------|------------------|
| B | 20/20 | 20/20 | 20/20 | 20/20 | 20/20 | **100/100 pass** | none (`failures=0` each batch; no `wpf-stability-failure-evidence-*.log`) |
| A | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ |

Phase **B** (trunk-line v2 base @ `10f7d6c`): v3 instrumentation ran end-to-end; **did not reproduce** the v2 locked / empty-`hostSessionId` / zero-`sessions` pattern in 100 consecutive WPF stability iterations. ~10s per pass (real polls, not harness instant-fail).

### Per-failure timelines (attempt 4)

_None on phase B (all green). Phase A pending — this is the branch stack where v2 captured global-25 locked evidence on PR42._

## STOP

Probe branches pushed; **no PR.** Phase B attempt 4 complete with no locked flake. Phase A attempt 4 determines whether PR42-line product delta is required to reproduce v2 failure; update this file when [44639252221](https://github.com/sadiloo/game-club-manager/actions/runs/44639252221) finishes.