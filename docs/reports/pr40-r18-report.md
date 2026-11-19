# PR #40 R18 report — Guard internet pipe listens before reconcile

## Root cause (file:line)

`src/GameClub.Kiosk.Guard/Internet/InternetPipeServer.cs` **`Run()`** blocked on **`InternetGuardStartupFirewall.ReconcileOnPipeStart`** (line 50, R15) and **`ReassertPersisted()`** (line 51) **before** creating `NamedPipeServerStream` for `CheckpointGuardInternet`. On CI, `RunResetScript()` inside reconcile can run long enough that the L17 probe saw **Guard pid 5804** but **no pipe response** until the xUnit **180 s** timeout (`pr40r17_kiosk.log`: `L17_PROBE_PIPE_WAIT` attempts 1–4, then job timeout).

**Hang vs slow (R17 evidence):** Client connect attempts every ~5 s with **no** `L17_PROBE_PIPE_READY` — consistent with **no listener**, not slow status. Watchdog `reconcile_start` / `reconcile_end` timing is expected on the next green run; reconcile is treated as **potentially unbounded** and capped with `CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS` (default 120 s) plus `reconcile_timeout` log lines.

## Fix

1. **Pipe first:** `Run()` enters the named-pipe accept loop immediately; reconcile runs on a background thread.
2. **Serialized firewall:** One lock (`_firewallGate`) for reconcile, `ReassertPersisted`, drift re-assert, and apply `IInternetFirewallApplier` calls — never concurrent.
3. **Bounded reconcile:** Background reconcile `Join(timeout)`; on timeout log `reconcile_timeout` and continue (pipe stays up).
4. **Status while reconciling:** `verb=status` returns `state=reconciling` without taking the firewall lock.
5. **Apply during reconcile:** Waits up to `CHECKPOINT_INTERNET_APPLY_WAIT_RECONCILE_MS` (default 30 s) for reconcile completion and lock; else **`reconcile_busy`** (retryable → kiosk `internetError`).
6. **Drift:** Drift thread starts only after reconcile completes (`_reconcileCompleted`).
7. **Client:** `GuardInternetClient` — bounded connect, write, and read (`pipe_*_timeout`); `KioskRuntime` records client failures on heartbeat `InternetApplied.Error`.
8. **Harness:** `GuardProcessLifecycle.StopGuardProcessTree` for L17 probe; CI prints `CI_PROCESS_LEAK_GUARD` / `CI_PROCESS_LEAK_KIOSK` and fails if any remain.

## Tests (CI names)

| Test | Phase |
|------|--------|
| `internet_pipe_status_ready_within_2s_while_reconcile_slow` | `dotnet-test-pc-internet-firewall` |
| `internet_pipe_apply_during_reconcile_returns_retryable_reconcile_busy_or_applies_after_wait` | same |
| `internet_guard_client_connect_timeout_surfaces_as_timeout_exception` | same |

## Files changed

- `src/GameClub.Kiosk.Guard/Internet/InternetPipeServer.cs`
- `src/GameClub.Kiosk.Guard/Internet/InternetGuardReconcilePolicy.cs` (new)
- `src/GameClub.Kiosk.Core/Guard/GuardInternetClient.cs`
- `src/GameClub.Kiosk.Core/Runtime/KioskRuntime.cs`
- `tests/.../WindowsPcInternetPipeReconcileIntegrationTests.cs` (new)
- `tests/.../SlowReconcileInternetFirewallApplier.cs`, `GuardProcessLifecycle.cs` (new)
- `tests/.../WindowsPcInternetApplyFailureLiveTests.cs`
- `scripts/windows/ci-integration.ps1`