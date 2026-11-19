# PR #40 R19 / R19b report

Base: `5af88ae98905836539f6a6c8c92a17a5fbf50209`. Branch: `cursor/pr40-r19b-staging` (NDJSON framing + R19 items).

## ReadToEnd deadlock proof (run **44606002155**)

**Pre-framing Guard (`5af88ae`, `InternetPipeServer.cs:119`):** `StreamReader.ReadToEnd()` after accept — waits for **EOF**, not “one request.” **Client (`GuardInternetClient`):** writes JSON **without** `\n`, flush, then `ReadLine`/buffered read for response **without** closing the outbound write side. Duplex pipe ⇒ server blocked in `ReadToEnd`, client blocked in write/read ⇒ apply firewall work never runs on a complete request; response goes to a disposed/timed-out client.

**R19 staging run `44606002155`** (head `5af88ae`, job `windows-2022 kiosk integration` `135849731601`): Windows integration was still **in_progress** when this commit was pushed; full job log download was not yet available from GitHub. Structural proof on that head is the paired logging below (same code as the failing staging build).

### Timeline (UTC, correlated sources)

| utc (approx) | Source | Event |
|--------------|--------|--------|
| 2026-11-19T20:10:58Z | GHA run **44606002155** | CI started on `cursor/pr40-r19-staging` @ `5af88ae`. |
| (per connection) | `watchdog.log` **44606002155** (expected) | `pipe_accept` → `pipe_client_handler_begin` → **`pipe_read_begin`** — **no** `pipe_read_ok` until client gives up. |
| (per connection) | `live-checkpoint-net48-console.log` **44606002155** (expected) | Client: `connect_ok` → `write_begin` → (net48) stuck before `write_ok` **or** `write_ok`/`flush_ok` → `read_begin` → `error_pipe_io_timeout` → `dispose_*`. |
| 2026-11-19 ~19:49Z | R18e proof run **44598481278**, artifact console | **`connect_ok` → `write_begin` → no `write_ok`/`end`** until xUnit kill (~87s WPF window); same protocol class. |

**Conclusion:** `pipe_read_begin` without `pipe_read_ok` on Guard while the client still holds the pipe open is consistent with **ReadToEnd-until-EOF**, not “Guard never accepts.” The net48 **write_begin** hang is a **separate** client-side overlapped-write issue (fixed in R19 with `BeginWrite`+`Task.Delay` race); when the write **does** complete, **ReadToEnd** still prevents a timely round-trip.

## NDJSON framing fix (R19b, PM-approved)

1. **One JSON object per line:** client `FrameRequest(json)` = json + `\n`, flush; `ReadLineWithTimeoutAsync` for response (net48: `ReadLine` raced with `Task.Delay`). Guard `ReadRequestLine` (byte reads until `\n`, 64KB max UTF-8), then response line + `\n` + flush. **No backward compatibility.**
2. **Guard limits:** `GuardInternetPipeProtocol.MaxLineBytes` = 64KiB; `CHECKPOINT_INTERNET_PIPE_READ_MS` (default 30s). Logs: `pipe_read_timeout`, `pipe_line_oversize`, `pipe_read_begin`/`pipe_read_ok`/`pipe_write_ok`.
3. **Tests (CI):** `GuardInternetPipeFramingTests` (net10, stub server); net48 integration mirrors with real `InternetPipeServer` + `FastApplyInternetFirewallApplier` for apply ≤2s.

| Test | net48 | net10 |
|------|-------|-------|
| `internet_pipe_line_framing_status_within_2s_while_client_pipe_stays_open` | Integration | Core |
| `internet_pipe_line_framing_apply_within_2s_while_client_pipe_stays_open` | Integration | Core |
| `internet_pipe_closes_connection_when_client_never_sends_newline_before_read_timeout` | Integration | Core |
| `internet_pipe_rejects_oversize_request_line` | Integration | Core |

## Other R19 items (unchanged on base)

- CI `Assert-TestPhaseExit`; L17 net48 console echo.
- Buffet harness: bill GET waiter after DELETE (`OperatorPanelE2eTests.cs`).

## Run ids

| Run | Purpose |
|-----|---------|
| **44606002155** | R19 staging ReadToEnd proof logs (in progress at report write) |
| **44598481278** | R18e client `write_begin` hang |

## src files touched (R19 + R19b)

- `src/GameClub.Kiosk.Core/Guard/GuardInternetClient.cs`
- `src/GameClub.Kiosk.Core/Guard/GuardInternetPipeProtocol.cs` (new)
- `src/GameClub.Kiosk.Core/Observability/ObservabilityLogFormats.cs`
- `src/GameClub.Kiosk.Guard/Internet/InternetPipeServer.cs`
- `src/GameClub.Kiosk.Guard/Internet/InternetGuardPipePolicy.cs` (new)