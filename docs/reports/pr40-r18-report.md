# PR #40 R18 report — Guard internet pipe listens before reconcile

## Root cause (file:line)

`src/GameClub.Kiosk.Guard/Internet/InternetPipeServer.cs` **`Run()`** blocked on **`InternetGuardStartupFirewall.ReconcileOnPipeStart`** (line 50, R15) and **`ReassertPersisted()`** (line 51) **before** creating `NamedPipeServerStream` for `CheckpointGuardInternet`. On CI, `RunResetScript()` inside reconcile can run long enough that the L17 probe saw **Guard pid 5804** but **no pipe response** until the xUnit **180 s** timeout (`pr40r17_kiosk.log`: `L17_PROBE_PIPE_WAIT` attempts 1–4, then job timeout).

**Hang vs slow (R17 evidence):** Client connect attempts every ~5 s with **no** `L17_PROBE_PIPE_READY` — consistent with **no listener** (synchronous reconcile blocked the accept loop), **not** a slow status reply. R17 did **not** capture `watchdog.log` reconcile step timings on the failing head; that cannot be classified as slow reconcile from timing data alone. This change adds `reconcile_step localhost_cleanup|readback_capture|reset_script|post_reset_readback` and `reconcile_steps_total` lines on every Guard start so the next CI run can prove where time is spent.

## Fix

1. **Pipe first:** `Run()` enters the named-pipe accept loop immediately; reconcile runs on a background thread.
2. **Serialized firewall:** One lock (`_firewallGate`) for reconcile, `ReassertPersisted`, drift re-assert, and apply `IInternetFirewallApplier` calls — never concurrent.
3. **Bounded reconcile:** Background reconcile `Join(timeout)`; on timeout log `reconcile_timeout` and continue (pipe stays up).
4. **Status while reconciling:** `verb=status` returns `state=reconciling` without taking the firewall lock.
5. **Apply during reconcile:** `TryEnter` up to `CHECKPOINT_INTERNET_APPLY_WAIT_RECONCILE_MS` (default **20 s**, below client IO **30 s**); else **`reconcile_busy`**. After **`reconcile_timeout`**, status stays **`reconcile_timeout`** while the work thread lives; apply still gets **`reconcile_busy`**.
6. **Pipe concurrency:** Each connected client is handled on its own thread so a blocked apply does not stall status.
7. **Drift:** Drift thread starts only after reconcile work finishes (`_reconcileWorkFinished`).
8. **Client:** `GuardInternetClient` — bounded connect, write, and read (`pipe_*_timeout`); `KioskRuntime` records client failures on heartbeat `InternetApplied.Error`.
9. **Harness:** `GuardProcessLifecycle.StopGuardProcessTree` for L17 probe; CI prints `CI_PROCESS_LEAK_GUARD` / `CI_PROCESS_LEAK_KIOSK` and fails if any remain.

## Tests (CI names)

| Test | Phase |
|------|--------|
| `internet_pipe_status_ready_within_2s_while_reconcile_slow` | `dotnet-test-pc-internet-firewall` |
| `internet_pipe_apply_during_reconcile_returns_retryable_reconcile_busy_or_applies_after_wait` | same |
| `internet_guard_client_connect_timeout_surfaces_as_timeout_exception` | same |
| `internet_pipe_status_reports_reconcile_timeout_while_work_thread_hung` | same |
| `internet_pipe_status_answers_within_2s_while_apply_holds_firewall_gate` | same |
| `PcInternet_server_apply_wait_default_is_below_client_io_timeout` | Kiosk.Core.Tests |

## Files changed

- `src/GameClub.Kiosk.Guard/Internet/InternetPipeServer.cs`
- `src/GameClub.Kiosk.Guard/Internet/InternetGuardReconcilePolicy.cs` (new)
- `src/GameClub.Kiosk.Core/Guard/GuardInternetClient.cs`
- `src/GameClub.Kiosk.Core/Runtime/KioskRuntime.cs`
- `tests/.../WindowsPcInternetPipeReconcileIntegrationTests.cs` (new)
- `tests/.../SlowReconcileInternetFirewallApplier.cs`, `GuardProcessLifecycle.cs` (new)
- `tests/.../WindowsPcInternetApplyFailureLiveTests.cs`
- `scripts/windows/ci-integration.ps1`

## R18b — PowerShell runner timeout + reconcile hang gate release

### Root cause (file:line)

- `InternetPowerShellRunner.cs:24-26` — stdout/stderr `ReadToEnd()` ran before `WaitForExit`, so reconcile/apply PowerShell could ignore the timeout, deadlock on pipes, and read `ExitCode` from a live process.
- `InternetPipeServer.cs:139-143` — reconcile work held `_firewallGate` for the entire `ReconcileOnPipeStart`. On `reconcile_timeout` only status reflected the hang; apply stayed `reconcile_busy` and drift never started until hung `Join` completed.

### Per-call budget table (defaults, `InternetPowerShellRunner.Run`)

Single source: `GuardInternetBudgetDefaults` in Core; Guard resolves env overrides per column.

**Reconcile worst path (orphan OFF + reassert `Apply`):**

| # | Call site | stepName | Env override | Budget ms |
|---|-----------|----------|--------------|-----------|
| 1 | `InternetGuardStartupFirewall` | localhost_cleanup | `CHECKPOINT_INTERNET_RECONCILE_LOCALHOST_CLEANUP_MS` | 8 000 |
| 2 | `InternetGuardStartupFirewall` | readback_capture | `CHECKPOINT_INTERNET_RECONCILE_READBACK_MS` | 8 000 |
| 3 | `InternetFirewallApplier` (reconcile reset) | reset_script | `CHECKPOINT_INTERNET_RECONCILE_RESET_MS` | 28 000 |
| 4 | `InternetGuardStartupFirewall` | post_reset readback | `CHECKPOINT_INTERNET_RECONCILE_POST_RESET_READBACK_MS` | 8 000 |
| 5 | reassert `Apply` → localhost cleanup | apply localhost | `CHECKPOINT_INTERNET_APPLY_LOCALHOST_CLEANUP_MS` | 4 000 |
| 6 | reassert `Apply` → reset or OFF script | apply reset/off | `CHECKPOINT_INTERNET_APPLY_RESET_MS` / `_OFF_` | 8 000 |
| 7 | reassert `Apply` → readback | apply readback | `CHECKPOINT_INTERNET_APPLY_READBACK_MS` | 8 000 |
| | **PowerShell budgets Σ** | | | **72 000** |
| | **+ kill-wait (7 × 3 000)** | | | **21 000** |
| | **Planned worst-case total** | | | **93 000** |
| | **+ planning margin** | | | **10 000** |
| | **Reconcile wall Join** | `CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS` | | **120 000** (**27 000** headroom) |

**Pipe apply path (single signed apply, worst OFF path):**

| # | Call site | Budget ms |
|---|-----------|-----------|
| 1–3 | localhost + firewall script + readback | 4 000 + 8 000 + 8 000 = **20 000** |
| | + kill-wait (3 × 3 000) | **9 000** |
| | **Apply worst-case** | **29 000** |
| | Client pipe IO (`DefaultIoTimeoutMs`) | **30 000** |

During active reconcile the kiosk does **not** stack gate wait and apply PowerShell in one IO window: `DefaultServerApplyWaitMs` (**20 000**) is only how long the Guard will wait on `_firewallGate` before returning `reconcile_busy`; full apply PowerShell runs on a later heartbeat after reconcile completes or abandons (`PcInternet_apply_powershell_worst_case_with_kill_waits_fits_client_io`).

### Tests (R18b → condition)

| Test | Condition |
|------|-----------|
| `internet_powershell_runner_times_out_hung_child_and_kills_process_tree` | hung PowerShell → `apply_failed:powershell_timeout` within budget; child pid gone |
| `internet_pipe_apply_after_hung_reconcile_timeout_removes_off_rules_when_server_sends_on` | orphan OFF + hung reconcile + timeout → ON apply clears `Checkpoint Internet Off` (0 rules) |
| `internet_drift_watchdog_starts_after_reconcile_timeout` | `watchdog.log` contains `drift_watchdog_start` after reconcile timeout |
| `internet_pipe_apply_succeeds_after_reconcile_timeout_while_reset_work_thread_hung` | status `reconcile_timeout` then apply `applied` |
| `internet_reconcile_abandoned_work_thread_makes_no_further_powershell_calls_after_apply` | after abandon, reconcile-thread `InternetPowerShellRunner` count unchanged |
| `PcInternet_apply_powershell_worst_case_with_kill_waits_fits_client_io` | Core constant contract |
| `PcInternet_reconcile_powershell_worst_case_with_kill_waits_fits_wall_with_margin` | Core constant contract |
| R18 matrix above | status ≤2s, reconcile apply window, client timeout, apply during slow apply unchanged |