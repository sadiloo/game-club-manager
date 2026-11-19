# PR #40 R18e diagnosis (no product fixes)

Red CI: run `44583927556` (ubuntu job `135771642703`, windows kiosk `135771643130`).

## (a) MONEY — `pr29_r2_buffet_only_checkout_remove_refreshes_visible_total`

| Field | Detail |
|--------|--------|
| Test | `tests/GameClub.Server.Tests/OperatorPanelE2eTests.cs:474` |
| Evidence | Run `44583927556` ubuntu: `Expected: 20000` / `Actual: 35000` on `bill.collectibleToman` after DELETE (چیپس 20_000 + نوشابه 15_000 = 35_000; remove last line → 20_000). |
| Main | Run `44276759016` @ `5574ce0` ubuntu job `134706749003` — no failure for this test (full green). |
| Diff vs main | `OperatorBuffetSaleService` / `RemoveLineFromOpenSaleAsync` / `RefreshOpenSalePendingBillIfAnyAsync` **unchanged**. `OperatorBillService` PC-internet bill segments (e.g. ~140+) do not apply to buffet-only `ComputeBuffetOnlyAsync`. |
| Isolated repro | Runs `44597546143` (pr40 diag) / `44597562592` (main diag): **0/5 failed**; each iteration `DIAG_BUFFET_*` shows `afterCollectible=20000`, `collectibleToman=20000` at remove time. |
| Stale GET on diag runs | **Not demonstrated** — all green; passing `:474` ⇒ `billRefresh` JSON had `collectibleToman=20000`. |
| Red `44583927556` | Still `Actual: 35000` at `:474`; server refresh mechanism not reproduced in isolation → suite-order / Playwright GET race **unproven** there, **src refresh ruled out** on diag evidence. |
| Class | **harness** (test response race under full suite) unless red re-fails with DIAG; **not src** buffet remove. |
| STOP (if src were implicated) | `OperatorBuffetServices.cs:670-686` — ensure refresh persists before DELETE returns (not applied). |
| Proposed diff (harness, do not apply) | `OperatorPanelE2eTests.cs:463-474` — assert on DELETE response + await explicit `GetBuffetOnlyBillAsync` after `deleteDone` with line-count in URL predicate. |

## (b) GUARD_PIPE

### (b1) Eight `pipe_connect_timeout` @ ~5s (18:30:50–18:33:22Z)

| Field | Detail |
|--------|--------|
| Evidence | Job `135771643130`: `GUARD_PIPE utc_ms=1795113050609 verb=apply event=start` → `event=end duration_ms=4992 error_code=pipe_connect_timeout` (and 7 similar through `1795113202726`). |
| Guard running? | **No** dedicated Guard for net10 server-integration harness: `CI_FIREWALL guard_rule_count=0` at `18:33:47` after net10-server phase; no `L17_PROBE_GUARD_STARTED` / Guard pid in job log for that window. Kiosk runtime applies without Guard listener → expected `pipe_connect_timeout`. |
| Class | **harness** (live server tests without Guard process). Not R18 pipe-thread product cause for this window. |

### (b2) Start without `end` (L17 + WPF)

| Field | Detail |
|--------|--------|
| `CHECKPOINT_INTERNET_PIPE_IO_MS` | Not set in `.github/workflows/ci.yml` or `scripts/windows/ci-integration.ps1`; client defaults `connect_ms=5000`, `io_ms=30000` (`GuardInternetClient.DefaultIoTimeoutMs`). |
| Red L17 | Artifact `14561774950`: `GUARD_PIPE utc_ms=1795113238960 verb=status event=start` only; xUnit **180000 ms** timeout; no `L17_GUARD_OFF_PROBE`. |
| Red WPF | Job `135771643130`: `GUARD_PIPE utc_ms=1795113463245 verb=apply event=start` (no `end`) ~18:37:43–18:39:10. |
| CI proof run | `44598481278` job windows kiosk, artifact `14570091439`, commit `3746864`. |
| `CHECKPOINT_INTERNET_PIPE_IO_MS` | **unset** → `config connect_ms=5000 io_ms=30000` (logged on every call). |
| **Proven block (L17)** | `live-checkpoint-net48-console.log`: `connect_ok` (thread=6) → `write_begin` → **no** `write_ok` / `end` until xUnit **180000 ms** timeout. |
| **Proven block (WPF night)** | Job log `44598481278`: `verb=apply utc_ms=1795117687321` `connect_ok thread=17` → `write_begin` → fail at `19:49:34` (~87s), no `write_ok`. |
| Cause | **(2) net48 `WriteWithTimeoutAsync`** — `await pipe.WriteAsync(..., timeoutCts.Token)` at `GuardInternetClient.cs:119` does not return within `io_ms`; cancellation is ignored once overlapped/BeginWrite is in flight on .NET Framework. Refuted: (1) connect returned `connect_ok`; (3) no `dispose_begin` without prior hang; (4) process alive until xUnit kill. |
| **STOP — minimal product diff (do not apply)** | `#if !NET10_0_OR_GREATER` in `WriteWithTimeoutAsync` / `FlushWithTimeoutAsync`: race `Task.Factory.FromAsync(BeginWrite/EndWrite, …)` (and sync `Flush` on a worker) against `Task.Delay(timeoutMs)` like `ReadWithTimeoutAsync`, then throw `IoTimeout` when delay wins. |

## (c) L17 net48 live-checkpoint

| Field | Detail |
|--------|--------|
| Test | `WindowsPcInternetApplyFailureLiveTests.live_server_pc_internet_guard_forced_apply_failure_shows_error_and_offline_at_press` |
| Failure | **Test execution timed out after 180000 ms** (not a failing assert). TRX: `kiosk-windows-live-checkpoint-net48.trx`. |
| L17_STEP (artifact console) | `test_begin` → `orphan_cleanup` → `guard_start_begin/end pid=7244` → `L17_PROBE_GUARD_STARTED` → `pipe_wait_begin` → `pipe_wait_attempt attempt=1` → then pipe hang. |
| `L17_GUARD_OFF_PROBE` | **Did not run** (test never left `pipe_wait`). |
| Job summary gate | `ci-integration.ps1:605-606` records `liveNet48Exit` in `FinalExitCode` but does **not** throw; script continues to night WPF; `:632` `throw "night WPF test failed"` ⇒ GHA step fails on **WPF**, masking L17 in summary. `ci-phase.log`: `END dotnet-test-net48-live-checkpoint exit=1`. |

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

| Branch | HEAD |
|--------|------|
| `cursor/pr40-r18e-diag` | `b3a57f007522c9dbfe793a1b8352bb7543085062` |
| `cursor/main-5574ce0-diag` | `2cd902f2be0417a028ac45f06129d4658080f1a5` |

### Diag workflow runs (in progress)

| Branch | Workflow | Run |
|--------|----------|-----|
| `cursor/pr40-r18e-diag` | `pr40-r18e-diag-money` | `44597546143` |
| `cursor/main-5574ce0-diag` | `pr40-r18e-diag-money` | `44597562592` |