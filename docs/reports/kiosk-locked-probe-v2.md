# Kiosk locked probe v2 (diagnosis only)

Base: `main` `5574ce0`. Branches: `cursor/kiosk-locked-probe-v2`, `cursor/kiosk-locked-probe-v2-pr42` (logging + CI/scripts only). No PR.

## External reference (PR #42 staging `13e5a4e`, no kiosk diff)

| Field | Value |
|--------|--------|
| Workflow run | [44609799325](https://github.com/sadiloo/game-club-manager/actions/runs/44609799325) |
| Job | [135863047760](https://github.com/sadiloo/game-club-manager/actions/runs/44609799325/job/135863047760) |

### A) Integration — `host_logout_open_ack_during_slow_post_stays_locked_without_session`

| UTC | Event |
|-----|--------|
| `2026-11-19T20:54:36Z` | FAIL — `Assert.True` expected `True`, actual `False` at **`KioskHostStatusTimerTests.cs:739`** (helper `:755`, scenario `:652`, `:662`). |
| `2026-11-19T20:54:38Z` | Integration **29/30** pass; step exit **1** (downstream steps use `if: always()`). |

**Meaning:** after second **Open** ack during `LogoutAsync` with `LogoutHttpDelay=800ms`, `_logoutInProgress` must stay **true** (`KioskHostStatusTimerTests.cs` **738**). Flake: flag already **false**, cleared in **`KioskHostService.LogoutAsync` `finally` at line 297** before slow POST window ends. Mutants `Host_logout_http_before_lock` / `Host_logout_no_guard_before_http` kill this test.

### B) Stability run 5/20 — `wpf_remaining_time_matches_funded_session_within_one_tick`

| UTC | Event |
|-----|--------|
| `2026-11-19T20:58:34Z` | Run **5/20** starts. |
| `2026-11-19T21:00:11Z` | FAIL **`WindowsKioskWpfSmokeTests.cs:186`** after **90.9s** poll. |

**Diagnostics:** `Locked — PC-WPF-1b4930b2 · connected`; transport **Connected**; `runtimeState=Locked`; `shellPhase=Locked`; **`hostSessionId`/`runtimeSessionId` all-zero**; `walletTopUpHttp=200`; `credentialPutHttp=204`. Symptom: **`KioskHostService.RefreshStationStatusText` lock path (~513–526)** without display; orchestrator fall-through **`KioskShellOrchestrator.cs:108`** (`lockedReason=other:empty_host_session` when HB_ACK logging present).

## Shared cause? (both failures)

| Test | Primary file:line | Mechanism |
|------|-------------------|-----------|
| Logout / slow POST | **`KioskHostService.cs:297`**, assert **`KioskHostStatusTimerTests.cs:738–739`** | `_logoutInProgress` drops early → Open ack path at **`OnHeartbeatAcknowledged` 354, 378** no longer blocks Playing while logout in flight. |
| Remaining-time stability | **`WindowsKioskWpfSmokeTests.cs:186`**, **`KioskShellOrchestrator.cs:79–108`** | Login/fund HTTP OK but **`_sessionId` stays empty** → Open acks → **Locked** UI with no `~hh:mm:ss left`; runtime **`KioskRuntime.cs:391–393`**. |

**Verdict:** **Same kiosk subsystem** (session bind vs heartbeat/logout ordering). **Not the same line or always the same flake:** on [44609799325](https://github.com/sadiloo/game-club-manager/actions/runs/44609799325) both failed in one job; phase 2 probe below shows **stability can fail while integration + 20× host-logout loop are green** — so co-failure is **not required**.

## Phase 1 — `cursor/kiosk-locked-probe-v2` @ `5574ce0`

| Run | Job id | Stability (b1/b2/b3) | Host logout 20× |
|-----|--------|----------------------|-----------------|
| [44620409125](https://github.com/sadiloo/game-club-manager/actions/runs/44620409125) | [135900224360](https://github.com/sadiloo/game-club-manager/actions/runs/44620409125/job/135900224360) | **0 / 0 / 0** (60/60) | **0/20** |

Full order (integration → mutations → orphan → host loop → 3×20 stability) passed.

## Phase 2 — `cursor/kiosk-locked-probe-v2-pr42` @ `32c9ad9`

| Run | Job id | Stability (b1/b2/b3) | Host logout 20× | Integration host suite |
|-----|--------|----------------------|-----------------|------------------------|
| [44625751429](https://github.com/sadiloo/game-club-manager/actions/runs/44625751429) | [135919000961](https://github.com/sadiloo/game-club-manager/actions/runs/44625751429/job/135919000961) | **0 / 1 / 0** | **0/20** | **30/30** pass (no `host_logout…` flake) |

Workflow also failed unrelated **`windows-2022 shell`** ([135919000273](https://github.com/sadiloo/game-club-manager/actions/runs/44625751429/job/135919000273)); kiosk job still produced probe evidence.

### Phase 2 stability failure timeline (global **25**, batch 2 run **5/20**)

| UTC | Event |
|-----|--------|
| `2026-11-19T22:31:27Z` | `STAB_STEP … run_begin` global=25 |
| `2026-11-19T22:33:03Z` | `WPF_STABILITY_FAIL global=25`; assert **`WindowsKioskWpfSmokeTests.cs:188`** (same poll contract as :186 on main). |
| Evidence | Station **`PC-WPF-6e5f9ab7`**; Postgres **`sessions` 0 rows** for that station at failure time; integration-step HB_ACK samples show **`lockedReason=other:empty_host_session`** with **`hostSessionId=000…000`** (`KioskShellOrchestrator.cs:108`). |

## STOP — minimal fix (report only; kiosk src; not applied)

1. **`KioskHostService.cs:297`** — keep `_logoutInProgress` true for full slow-logout + relock window (`KioskHostStatusTimerTests.cs:738`).
2. **`KioskHostService.cs:368–375`** + **`KioskShellOrchestrator.cs:79–108`** — after successful login, bind/adopt session before Open acks paint **Locked** lock screen; do not leave `hostSessionId` empty while transport is connected.