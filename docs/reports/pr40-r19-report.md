# PR #40 R19 report

Base: `24f04f01ba751f4fb4eeb923def465c5c64174e1`. Staging: `cursor/pr40-r19-staging`.

## (1) net48 client write/flush timeout

**Root cause (R18e run `44598481278`):** On .NET Framework 4.8, `NamedPipeClientStream.WriteAsync` with `CancellationTokenSource.CancelAfter` does not complete when the cancel fires if an overlapped `BeginWrite` is already in flight — kiosk console showed `connect_ok` → `write_begin` → no `write_ok` / `end` until xUnit killed the run at 180s (`docs/reports/pr40-r18e-diag.md` §b2).

**Fix:** `src/GameClub.Kiosk.Core/Guard/GuardInternetClient.cs` — under `!NET10_0_OR_GREATER`, `WriteWithTimeoutAsync` / `FlushWithTimeoutAsync` race `Task.Factory.FromAsync(BeginWrite/EndWrite)` and a worker `Flush()` against `Task.Delay(timeoutMs)`, matching `ReadWithTimeoutAsync`. `SendAsync` logs sub-steps (`connect` / `write` / `flush` / `read` / `dispose`) via `ObservabilityLogFormats.WriteGuardPipeSubStep`; `finally` disposes the pipe on timeout or error.

**Test:** `internet_guard_client_write_timeout_when_server_accepts_but_never_reads` — stub `NamedPipeServerStream` accepts and never reads; client fails with `pipe_io_timeout` within IO budget (`dotnet-test-pc-internet-firewall`, net48).

## (2) Guard-side root cause (b3)

**Proof (client hang, not Guard read stall):** R18e proved the client stuck **before** Guard could drain (`write_begin` with no server read required for that symptom). Guard already accepts on the listener thread and handles each client on `CheckpointInternetPipeClient` with `ReadToEnd()` **before** `HandleRequest` / `_firewallGate` (`InternetPipeServer.cs:106-118`, `HandleRequest` for `status` never takes the gate).

**Observability added (no behavioral change required):** `watchdog.log` lines `pipe_accept`, `pipe_client_handler_begin`, `pipe_read_begin`, `pipe_read_ok`, `pipe_gate_wait_begin|exit`, `pipe_gate_enter`, `pipe_gate_exit` with managed thread ids — so the next CI failure can show read starting immediately after accept while apply-only paths log gate waits separately.

### Guard diff (PM review)

| File | Lines | Change |
|------|-------|--------|
| `InternetPipeServer.cs` | 90-91 | Log `pipe_accept` after `WaitForConnection` |
| `InternetPipeServer.cs` | 108-118 | Handler / `read_begin` / `read_ok` logging |
| `InternetPipeServer.cs` | 315-332 | Apply gate wait/enter logging |
| `InternetPipeServer.cs` | 306-308 | `pipe_gate_exit` in apply `finally` |

## (3) CI script exit codes

`scripts/windows/ci-integration.ps1`: `Assert-TestPhaseExit` throws on any nonzero phase exit (names the phase). Removed deferred `FinalExitCode` aggregation that let L17 net48 fail while night WPF became the visible failure. After L17 net48, echoes `live-checkpoint-net48-console.log` to the job log.

## (4) Buffet harness

`tests/GameClub.Server.Tests/OperatorPanelE2eTests.cs:463-472` — register bill GET `WaitForResponseAsync` **after** DELETE completes (fixes stale GET race; assert unchanged at `:474`).

## Run ids

| Item | Id |
|------|-----|
| R18e diag reference | `44598481278` |
| R18e money/buffet diag | `44583927556` |
| Staging CI | _(pending push — await GATE OK)_ |

## Files changed

- `src/GameClub.Kiosk.Core/Guard/GuardInternetClient.cs`
- `src/GameClub.Kiosk.Core/Observability/ObservabilityLogFormats.cs`
- `src/GameClub.Kiosk.Guard/Internet/InternetPipeServer.cs`
- `scripts/windows/ci-integration.ps1`
- `tests/GameClub.Server.Tests/OperatorPanelE2eTests.cs`
- `tests/GameClub.Kiosk.Windows.IntegrationTests/Guard/WindowsPcInternetPipeReconcileIntegrationTests.cs`
- `docs/reports/pr40-r19-report.md`