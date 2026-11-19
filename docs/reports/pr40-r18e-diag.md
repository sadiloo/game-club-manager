# PR #40 R18e diagnosis (no product fixes)

Red CI: run `44583927556` (ubuntu job `135771642703`, windows kiosk `135771643130`).

## (a) MONEY — `pr29_r2_buffet_only_checkout_remove_refreshes_visible_total`

| Field | Detail |
|--------|--------|
| Test | `tests/GameClub.Server.Tests/OperatorPanelE2eTests.cs:474` |
| Evidence | Run `44583927556` ubuntu: `Expected: 20000` / `Actual: 35000` on `bill.collectibleToman` after DELETE (چیپس 20_000 + نوشابه 15_000 = 35_000; remove last line → 20_000). |
| Main | Run `44276759016` @ `5574ce0` ubuntu job `134706749003` — no failure for this test (full green). |
| Diff vs main | `OperatorBuffetSaleService` / `RemoveLineFromOpenSaleAsync` / `RefreshOpenSalePendingBillIfAnyAsync` **unchanged**. `OperatorBillService` PC-internet bill segments (e.g. ~140+) do not apply to buffet-only `ComputeBuffetOnlyAsync`. |
| Cause | Stale `PendingBill.CollectibleToman` (35_000) on GET `/api/operator/buffet-only/{id}` after line remove — not an extra 15_000 “internet” line in the composed bill. PR-only under heavier ubuntu suite; mechanism unproven until diag CI repeats. |
| Class | **harness** (suspected Playwright `WaitForResponseAsync` on GET vs DELETE ordering / stale response); **src** ruled out by zero buffet-server diff. |
| STOP (if src were implicated) | `OperatorBuffetServices.cs:670-686` — ensure refresh persists before DELETE returns (not applied). |
| Proposed diff (harness, do not apply) | `OperatorPanelE2eTests.cs:463-474` — assert on DELETE response + await explicit `GetBuffetOnlyBillAsync` after `deleteDone` with line-count in URL predicate. |

## (b) GUARD_PIPE

### (b1) Eight `pipe_connect_timeout` @ ~5s (18:30:50–18:33:22Z)

| Field | Detail |
|--------|--------|
| Evidence | Job `135771643130`: `GUARD_PIPE utc_ms=1795113050609 verb=apply event=start` → `event=end duration_ms=4992 error_code=pipe_connect_timeout` (and 7 similar through `1795113202726`). |
| Guard running? | **No** dedicated Guard for net10 server-integration harness: `CI_FIREWALL guard_rule_count=0` at `18:33:47` after net10-server phase; no `L17_PROBE_GUARD_STARTED` / Guard pid in job log for that window. Kiosk runtime applies without Guard listener → expected `pipe_connect_timeout`. |
| Class | **harness** (live server tests without Guard process). Not R18 pipe-thread product cause for this window. |

### (b2) Start without end `utc_ms=1795113463245` (18:37:43Z)

| Field | Detail |
|--------|--------|
| File:line | `src/GameClub.Kiosk.Core/Guard/GuardInternetClient.cs:129-137` (`ReadWithTimeoutAsync`) |
| Evidence | `GUARD_PIPE utc_ms=1795113463245 verb=apply event=start` with **no** matching `event=end` before `wpf_remaining_time…` failed at `18:39:10` (96s). Test uses **net48** client `#else` path. |
| Cause | `ReadAsync` uses only caller `CancellationToken`, not `timeoutCts.Token`; orphan read can outlive `WhenAny` timeout and prevent normal `event=end` logging / stall WPF session apply path. |
| Class | **src** (product timeout defect on net48 read path). |
| STOP — proposed minimal diff (do not apply) | At `GuardInternetClient.cs:129`, pass linked timeout token into `ReadAsync`; cancel read task on timeout branch. |

## (c) L17_STEP = 0

| Field | Detail |
|--------|--------|
| Evidence | Job `135771643130`: zero `L17_STEP` in Actions log; `dotnet-test-net48-live-checkpoint` `START 18:33:49Z` → `END 18:36:58Z exit=1`; test listed: `live_server_pc_internet_guard_forced_apply_failure…`. |
| Cause | L17 ran under net48 `Category=LiveCheckpointServer` but **console step lines are not surfaced** in the published GHA step log (only phase markers + `LEASE_BUILD` scrape). Phase **failed** (`exit=1`) — gate violation: L17 probe did not complete successfully. |
| Class | **CI** (log capture) + **harness** (failed L17 phase; probe details in artifact `live-checkpoint-net48-console.log`, artifact `14561774950`). |
| `L17_GUARD_OFF_PROBE` | Not observed in GHA stdout; probe never reached success string in captured log. |

## (d) WPF night line `WindowsKioskWpfSmokeTests.cs:290`

| Field | Detail |
|--------|--------|
| Evidence | `Assert.False` — empty `capturedStatus` after 90s poll (`18:37:43` `GUARD_PIPE start` → fail `18:39:10`). Prior `HB_ACK` `leaseVerify=fail:null_lease` at `18:31:05` during earlier server phase. |
| LEASE_BUILD lines | `band=none` `hourlyRateToman=80000` on sessions `7d8c1ef4…`, `cd7a9932…`, `e4c44adc…`, `4403e16a…` — relaunch / evidence scrape from **dotnet-test-net10-server** and **night-wpf** server logs (not night-band oracle). Night test uses `CHECKPOINT_SERVER_PC_BAND=night` from `ci-integration.ps1:617`. |
| Cause | Kiosk never showed `hh:mm:ss left` — consistent with blocked/hung Guard apply (b2) and prior pipe timeouts leaving session/lease unhealthy. |
| Class | **harness/CI consequence** of (b); not a standalone money/tariff bug. |

## Diag branches

| Branch | Base | Purpose |
|--------|------|---------|
| `cursor/pr40-r18e-diag` | `24f04f0` | `DIAG_BUFFET_*` logging + `pr40-r18e-diag-money.yml` 5× buffet test |
| `cursor/main-5574ce0-diag` | `5574ce0` | Same logging/workflow on main for comparison |

Update with commit SHAs and workflow run ids after push.