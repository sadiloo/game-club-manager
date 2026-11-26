# PR #40 R18d report — observability only

**No behavior change.** Logging and CI artifact paths only (no timing, retry, budget, lease, firewall, money, or assertion changes). `src/GameClub.Server/Kiosk/KioskServices.cs:262-265` untouched.

## New log lines (file:line)

| Prefix | Location | Purpose |
|--------|----------|---------|
| `GUARD_PIPE` | `src/GameClub.Kiosk.Core/Guard/GuardInternetClient.cs` (SendAsync) | Pipe client `start` / `end` with `duration_ms` and stable `error_code` |
| `GUARD_PIPE` | `src/GameClub.Kiosk.Core/Observability/ObservabilityLogFormats.cs:WriteGuardPipeStep` | Formatter |
| `HB_ACK` | `src/GameClub.Kiosk.Core/Observability/ObservabilityLogFormats.cs:WriteHbAck` | Heartbeat ack dump |
| `HB_ACK` | `src/GameClub.Kiosk.Core/Runtime/KioskShellOrchestrator.cs:OnHeartbeatAck` / `WriteHbAckLine` | Emitted on every orchestrator ack |
| `L17_STEP` | `tests/.../Server/L17StepLog.cs` | Harness step helper |
| `L17_STEP` | `tests/.../Server/L17GuardProbeSupport.cs` | Preflight, pipe wait attempts, cleanup |
| `L17_STEP` | `tests/.../Server/L17RealInternetOffProbe.cs` | Seed, hub, apply, readback, restore |
| `L17_STEP` | `tests/.../Server/WindowsPcInternetApplyFailureLiveTests.cs` | Test lifecycle, guard, harness pipe, cleanup |
| `L17_STEP` | `tests/.../Guard/GuardProcessLifecycle.cs` | Process-tree kill |
| `STAB_STEP` | `tests/.../Server/L17StepLog.cs` (`StabStepLog`) | Stability helper |
| `STAB_STEP` | `tests/.../Wpf/WindowsKioskWpfSmokeTests.cs` | `within_one_tick` begin/end |
| `STAB_STEP` | `scripts/windows/ci-integration-wpf-stability.ps1` | Per-run begin/end |
| (artifacts) | `scripts/windows/ci-integration.ps1` | `live-checkpoint-*.log` capture |
| (artifacts) | `.github/workflows/ci.yml` | Upload `live-checkpoint-*.log` + existing stability logs (`if: always()`) |

### HB_ACK fields

`utc_ms`, `ackSessionStatus`, `ackLeaseSessionId`, `hostSessionId`, `runtimeSessionId` (optional 4th arg to `OnHeartbeatAck`; WPF passes `KioskRuntime.BoundSessionId`), `leaseVerify` (`ok` or `fail:<detail>`), `leaseSessionId`, `leaseKeyHash8` (SHA256 of lease signature, first 8 hex — never raw key), `shellPhase`, and `lockedReason` when `shellPhase=Locked` (`status_not_open`, `lease_verify_failed`, `lease_session_mismatch`, `no_lease`, `other:*`).

### API surface (observability)

- `KioskRuntime.BoundSessionId` (read-only)
- `OnHeartbeatAck(..., Guid runtimeSessionId = default)` — optional 4th parameter; default preserves prior call sites

## Compile gate

Linux Release build with `-p:EnableWindowsTargeting=true` on: IntegrationTests, TestHost, Server.Tests, Kiosk.Core.Tests, Kiosk.Guard.

## Not done

- Fast-forward `cursor/pc-internet-switch-6bdd` — waits for `GATE OK <sha>` from PM
- NDJSON pipe framing (explicitly out of scope)