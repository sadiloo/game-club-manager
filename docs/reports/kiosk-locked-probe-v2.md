# Kiosk locked probe v2 (diagnosis only)

Base: `main` `5574ce0`. Branches: `cursor/kiosk-locked-probe-v2` (logging + CI only). No PR. Phase 2 (`cursor/kiosk-locked-probe-v2-pr42`) runs only if phase 1 records **0** stability failures.

## External reference run (PR #42 staging, no kiosk diff)

| Field | Value |
|--------|--------|
| Head | `13e5a4ed2137b852e2519809d17ddf8a743ec4d8` |
| Workflow run | [44609799325](https://github.com/sadiloo/game-club-manager/actions/runs/44609799325) |
| Job | `windows-2022 kiosk integration` ([135863047760](https://github.com/sadiloo/game-club-manager/actions/runs/44609799325/job/135863047760)) |
| Job outcome | **failure** |

### Integration-step failure (before stability)

| UTC | Event |
|-----|--------|
| `2026-11-19T20:47:27Z` | `ci-integration.ps1` starts (full net48 WPF host suite in job). |
| `2026-11-19T20:54:35Z` | Prior host tests pass; `host_logout_login_during_slow_post_readopts_new_session_on_next_ack` passes. |
| `2026-11-19T20:54:36Z` | **`host_logout_open_ack_during_slow_post_stays_locked_without_session` FAIL** — `Assert.True` expected `True`, actual `False` at `KioskHostStatusTimerTests.cs:739` (via `RunLogoutOpenAckDuringSlowPostOnDispatcherAsync` → `:755` → scenario `:652` → `:662`). |
| `2026-11-19T20:54:38Z` | Integration TRX **29 passed / 1 failed**; step exit **1** (mutations/orphan/stability still run via `if: always()` on downstream steps). |

**Assertion meaning (main `5574ce0` code):** after a second **Open** heartbeat ack while `LogoutAsync` is in flight with `LogoutHttpDelay=800ms`, the test requires `_logoutInProgress == true` (`KioskHostStatusTimerTests.cs` ~738). The flake is `_logoutInProgress` already **false** — cleared in `KioskHostService.LogoutAsync` `finally` at **`KioskHostService.cs:297`** while slow logout is still supposed to be in progress.

Mutants `Host_logout_http_before_lock` and `Host_logout_no_guard_before_http` both list this test as killer (job log ~20:54:44Z).

### Stability failure (after full integration order)

| UTC | Event |
|-----|--------|
| `2026-11-19T20:57:22Z` | WPF stability loop starts (post integration, mutations, orphan). |
| `2026-11-19T20:57:47Z`–`20:58:33Z` | Stability runs **1–4/20** pass (~8–9s each). |
| `2026-11-19T20:58:34Z` | Stability run **5/20** starts. |
| `2026-11-19T21:00:11Z` | Run **5/20** FAIL — assert `WindowsKioskWpfSmokeTests.cs:186` after **90.9s** poll. |

**Diagnostics (job log):** `lastStatusText='Locked — PC-WPF-1b4930b2 · connected'`; `IsTransportConnected=True`; `runtimeState=Locked`; `shellPhase=Locked`; **`hostSessionId=00000000-0000-0000-0000-000000000000`**; **`runtimeSessionId=00000000-0000-0000-0000-000000000000`**; `walletTopUpHttp=200`; `credentialPutHttp=204`.

Setup funding HTTP succeeded; kiosk stayed on **Locked** lock-screen copy with transport **connected** and **no** bound session for remaining-time display (`KioskHostService.RefreshStationStatusText` lock path ~513–526; no `~hh:mm:ss left`).

With observability (probe branch only), `OnHeartbeatAck` with empty host `sessionId` falls through to **`KioskShellOrchestrator.cs:108`** (`Locked`; `lockedReason=other:empty_host_session`). Runtime mirror: **`KioskRuntime.cs:391–393`** (`_sessionId == Guid.Empty` → `Locked`).

No `HB_ACK` lines on staging head `13e5a4e` (logging not on that commit).

## Do both failures share a cause?

**Same subsystem (kiosk session + lock + heartbeat/logout interleaving), not the same single line.**

| Failure | Primary locus | Mechanism |
|---------|----------------|-----------|
| `host_logout_open_ack_…` | `KioskHostService.cs:257–297` (`_logoutInProgress`), assert `KioskHostStatusTimerTests.cs:738–739` | Slow-logout guard drops too early → Open ack handling no longer treats logout as in-flight (`OnHeartbeatAcknowledged` Open branch uses `!_logoutInProgress` at **354**, **378**). |
| `wpf_remaining_time…` | `WindowsKioskWpfSmokeTests.cs:186`; orchestrator **`KioskShellOrchestrator.cs:79–108`**; UI **`KioskHostService.cs:513–526`** | After fund/login HTTP **200/204**, **`_sessionId` remains empty** through poll → Open acks resolve to **Locked**, status shows **Locked — · connected** without lease display. |

**Coupling on run 44609799325:** integration step already failed the logout/ack contract at **20:54:36Z**; stability failed **5/20** at **21:00:11Z** in the **same job** after mutations/orphan (leftover **process/DB state from earlier steps** possible; pre-batch registry not captured on that run). Both are **kiosk src** timing/state bugs, not harness assertion mistakes or transport-down.

**Verdict:** **Related family** — session lifecycle vs heartbeat/logout ordering — **not proven identical root cause** without probe `HB_ACK` on a matching stability failure. The logout flake is a **direct** regression on `_logoutInProgress` lifetime; the stability flake is **empty `hostSessionId` + Locked shell** (login bind or post-login clear).

## Probe v2 CI (this agent)

| Item | Value |
|------|--------|
| Branch | `cursor/kiosk-locked-probe-v2` |
| Commits | cherry-pick `b83f578` + CI scripts (`502a042` …) |
| Order | postgres → integration → reconnect mutations → orphan → **20× `host_logout_open_ack_during_slow_post_stays_locked_without_session`** → 3×20 WPF stability (`FAIL_LATE`, pre-batch diagnostics, failure evidence) |

### Phase 1 runs

| Run | Head SHA | Job id | Status | Stability failures (batch 1/2/3) | Host logout loop failures |
|-----|-----------|--------|--------|-----------------------------------|---------------------------|
| *(fill when CI completes)* | | | | | |

### Phase 2

Not started unless phase 1 stability totals **0/60** failures.

## STOP — minimal fix (report only, not applied)

1. **Logout guard:** keep `_logoutInProgress` true until logout HTTP + session clear complete (defer `finally` at `KioskHostService.cs:297` or set flag only after post-await relock), so Open acks during slow POST cannot widen session state (`KioskHostStatusTimerTests.cs:738` contract).

2. **Stability / empty session:** after successful `LoginAsync` (`KioskHostService.cs:189–191`), ensure heartbeat path cannot leave `_sessionId` empty while Open lease acks arrive — e.g. bind from lease when login response and ack race (`KioskHostService.cs:368–375`) or gate `RefreshStationStatusText` lock copy until first verified Open ack with display (`ApplyHeartbeatDisplay` ~320–328).

Re-run probe v2 after fix; expect both **20/20 host loop** and **60/60 stability** green in real CI order.