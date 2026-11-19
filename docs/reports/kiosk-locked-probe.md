# Kiosk locked-after-fund probe (main `5574ce0`)

## Goal

Reproduce the intermittent WPF stability failure  
`wpf_remaining_time_matches_funded_session_within_one_tick`  
(Status stays **Locked** for 90s with transport **Connected**, no `hh:mm:ss left` in `StatusText`) on **main** without product fixes—only observability.

## Probe setup

| Item | Value |
|------|--------|
| Base commit | `5574ce00962386881595ca5867b1a8d79e93d3c` (merge PR #39) |
| Branch | `cursor/kiosk-locked-probe` (no PR opened) |
| Observability | Logging-only port: `ObservabilityLogFormats` + `HB_ACK` in `KioskShellOrchestrator.OnHeartbeatAck` (from PR40 `24f04f0` pattern) |
| CI | GitHub `windows-2022`, 3×20-run batches (`WPF_STABILITY_FAIL_LATE=1`), linux/windows jobs skipped |
| Server under test | `GameClub.Server.dll` (same as main CI stability harness) |

## CI result (this probe)

| Field | Value |
|-------|--------|
| Workflow run | [44609528679](https://github.com/sadiloo/game-club-manager/actions/runs/44609528679) |
| Job | `windows-2022 kiosk integration` (job `135862094084`) |
| Outcome | **success** |
| Failures | **0 / 60** |
| Batch 1 | runs 1–20, `failures=0`, step ended `20:47:31Z` |
| Batch 2 | runs 21–40, `failures=0`, step ended `20:51:22Z` |
| Batch 3 | runs 41–60, `failures=0`, step ended `20:55:12Z` |

Per-run `WPF_STABILITY_PASS global=N` for N=1..60 is in the job log.  
`HB_ACK` lines are written to redirected per-run logs (`artifacts/kiosk-windows-logs/wpf-stability-run-*.log`); the CI “print probe stability summary” step did not surface them because every run passed (no failure excerpts were required).

## Answer: does main have the flake in this sample?

**Not in this 60-run sample on `5574ce0`.**  
The symptom described in the brief (PR #42 [44601236174](https://github.com/sadiloo/game-club-manager/actions/runs/44601236174), stability run **5/20**, assert at `WindowsKioskWpfSmokeTests.cs:186`) is **intermittent**. This probe did not hit it at 60× the usual loop size.

## Prior failure signature (from brief / PR #42)

When it fails:

- **Assert**: `WindowsKioskWpfSmokeTests.cs:186` — `capturedStatus` empty after 90s poll (`StatusText` must show server-driven remaining time).
- **Harness diagnostics** (poll failure path, same file `BuildRemainingTimePollDiagnostics`):  
  `lastStatusText` like `Locked — PC-WPF-… · connected`;  
  `IsTransportConnected=true`; `runtimeState=Locked`; `shellPhase=Locked`.
- **Class when failing**: **kiosk src** (UI/orchestrator/runtime), not harness assertion logic and not “transport down”. Server may still be relevant if acks lack lease/display, but the stuck **Locked** label is decided client-side.

## File:line map for “stays Locked” (main `5574ce0` code)

Use `HB_ACK … lockedReason=…` from a failing run to pick the branch.

| `lockedReason` (observability) | Where Locked is chosen | Mechanism |
|-------------------------------|------------------------|-----------|
| `other:empty_host_session` | `KioskShellOrchestrator.cs` fall-through ~93–94 | `OnHeartbeatAck`: `SessionStatus=Open` but `hostSessionId==Guid.Empty` (or not taken into Open branch) → `Phase=Locked` |
| `no_lease` | same fall-through after Open check | Open status but `ack.Lease` null → cannot enter Playing (~86–90) |
| `lease_verify_failed` | ~80–84 | `VerifyLease` false → `LeaseExpired` (different UI path; less common for “Locked —” string) |
| `lease_session_mismatch` | ~72–77 | Lease session id ≠ host `_sessionId` |
| `status_not_open` | ~93–94 | Heartbeat ack session status not `Open` while shell already Locked |
| (runtime `State=Locked`) | `KioskRuntime.cs` ~391–393 | `ApplyAcknowledgement`: `_sessionId==Guid.Empty` → `KioskRuntimeState.Locked` even if transport connected |

**UI “Locked —” without remaining time** (symptom):

- `KioskHostService.RefreshStationStatusText` ~512–526 — local lock / empty session with lock panel visible → `KioskStationStatus.FormatLocalLockScreen` (no `~hh:mm:ss left`).
- Else ~529–540 — `KioskStationStatus.Resolve` only shows remaining time when `_lastDisplay?.EstimatedRemainingSeconds` is set from heartbeat display (~320–328 `ApplyHeartbeatDisplay`).

**Test poll** (failure site): `WindowsKioskWpfSmokeTests.cs` ~140–186.

## HB_ACK timeline (this probe)

No failing runs → **no per-failure UTC timelines**. On a failing rerun, collect:

1. `STAB_STEP … within_one_tick_begin` from test stdout/log.
2. All `HB_ACK` lines from the same run log (stdout captured in `wpf-stability-run-N.log`).
3. Last `HB_ACK` before 90s deadline with `shellPhase=Locked` and `lockedReason=…`.

## STOP: kiosk src — minimal fix (not applied)

Per brief: diagnosis only; **do not merge** this without a reproduced `lockedReason` on main.  
When the flake matches **empty host / late bind** (`other:empty_host_session` + `runtimeState=Locked` after successful login HTTP), the smallest kiosk-side hardening direction is:

- Ensure the first post-login heartbeat treats `ack.Lease` + open status as authoritative for `_sessionId` and orchestrator phase before `RefreshStationStatusText` runs (today partial adoption exists in `OnHeartbeatAcknowledged` ~368–377, but fall-through Locked can win if ack ordering races login).

Concrete minimal diff to **propose** (not applied on this branch):

1. In `KioskHostService.OnHeartbeatAcknowledged`, when `phase==Locked`, `ack.SessionStatus==Open`, lease verifies, and `_sessionId==Guid.Empty`, adopt `ack.Lease.Payload.SessionId` before `RefreshStationStatusText` (mirror the existing branch at ~368–377).
2. Add a single WPF integration test that simulates login completing one tick after a lease-bearing ack (regression for empty host session).

**Server src** minimal fix only if failing logs show `ackSessionStatus` not `Open` or missing lease while DB session is open—would point at heartbeat/lease issuance on server; not evidenced in this 60/60 green probe.

## Artifacts

- Run log tail / summary: workflow **44609528679**, step “print probe stability summary”.
- Uploaded artifact: `kiosk-windows-integration` (includes `wpf-stability-summary.log` and per-run logs if present).

## Next steps (if flake must be caught)

- Re-run probe batches until at least one failure, or run probe **after** full `windows kiosk integration` (closer to PR #42 job mix—integration tests run before stability there).
- On failure, parse `wpf-stability-run-N.log` for `HB_ACK` + `Diagnostics:` block from assert message.