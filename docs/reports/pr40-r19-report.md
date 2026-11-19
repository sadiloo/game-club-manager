# PR #40 R19 / R19b report

Branches: `cursor/pr40-r19-staging` (`5af88ae`), `cursor/pr40-r19b-staging` (NDJSON framing).

## Root cause on `5af88ae` (file:line)

**Primary (44606002155 symptom — no `pipe_read_begin`, client stuck on `write_begin`):** In `InternetPipeServer.HandleConnectedClient`, **`5af88ae` constructed `new StreamWriter(server, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true }` before logging `pipe_read_begin` and before reading the request.** With default `StreamWriter` encoding, **`Encoding.UTF8` emits a 3-byte BOM** (`EF BB BF`) on first flush. **`AutoFlush = true` flushes that preamble into the duplex pipe while the client is still sending its request**, so the server blocks in the writer/reader setup and **`pipe_read_begin` is never logged** (watchdog shows only `pipe_client_handler_begin`). The client never reaches **`write_ok`** — it stalls on **`write_begin`** until `pipe_io_timeout`.

**Secondary:** After a full request was (hypothetically) read, **`StreamReader.ReadToEnd()`** waited for **EOF** on an open duplex pipe while the client waited for a response → apply/handler never completed a round-trip.

**`f0fcdb4` fixes:** No `StreamWriter` on the wire — response via **`Encoding.UTF8.GetBytes`** (no BOM). Request via **`GuardInternetPipeLineReader`** (NDJSON line, no `ReadToEnd`).

## (4) Literal timeline — run **44606002155** (`5af88ae98905836539f6a6c8c92a17a5fbf50209`)

| Field | Value |
|-------|-------|
| **Run id** | [44606002155](https://github.com/sadiloo/game-club-manager/actions/runs/44606002155) |
| **Windows job id** | `135849731601` (`windows-2022 kiosk integration`, conclusion: **failure**) |
| **Artifact id** | `14573017605` (`kiosk-windows-integration`) |
| **L17 artifact files** | `watchdog.log`, `kiosk-windows-logs/live-checkpoint-net48-console.log` |

### L17 attempt 1 — correlated Guard + client (same connection window)

| UTC (log timestamp) | Source (artifact / job log) | Literal line |
|---------------------|----------------------------|--------------|
| 2026-11-19T20:31:55.6726369Z | `watchdog.log` | `pipe_accept thread=3` |
| 2026-11-19T20:31:55.6726369Z | `watchdog.log` | `pipe_client_handler_begin thread=9` |
| *(none)* | `watchdog.log` | **No** `pipe_read_begin` / **no** `pipe_read_ok` (BOM/autoflush before read on `5af88ae`) |
| 2026-11-19T20:31:55.265Z (`utc_ms=1795120315265`) | `live-checkpoint-net48-console.log` | `GUARD_PIPE … verb=status event=start` |
| 2026-11-19T20:31:55.672Z (`utc_ms=1795120315672`) | `live-checkpoint-net48-console.log` | `GUARD_PIPE … verb=status event=connect_ok thread=6` |
| 2026-11-19T20:31:55.672Z (`utc_ms=1795120315672`) | `live-checkpoint-net48-console.log` | `GUARD_PIPE … verb=status event=write_begin thread=6` |
| *(absent ~30s)* | `live-checkpoint-net48-console.log` | **No** `write_ok` / `flush_ok` / `read_begin` |
| 2026-11-19T20:32:25.673Z (`utc_ms=1795120345673`) | `live-checkpoint-net48-console.log` | `GUARD_PIPE … verb=status event=error_pipe_io_timeout thread=10` |
| 2026-11-19T20:32:25.673Z (`utc_ms=1795120345673`) | `live-checkpoint-net48-console.log` | `GUARD_PIPE … verb=status event=dispose_begin` / `dispose_ok` |

Attempts 2–3: same pattern (`connect_ok` → `write_begin` → ~30s → `pipe_io_timeout`, no `write_ok`); job log step `dotnet-test-net48-live-checkpoint` echoes console at 2026-11-19T20:33:35Z.

### Later watchdog accepts (still no `pipe_read_ok`)

| UTC | `watchdog.log` |
|-----|----------------|
| 2026-11-19T20:32:26.1854677Z | `pipe_accept thread=3` / `pipe_client_handler_begin thread=15` |
| 2026-11-19T20:32:56.7089116Z | `pipe_accept thread=3` / `pipe_client_handler_begin thread=16` |

## NDJSON framing (R19b)

One JSON object per line; `GuardInternetPipeLineReader` (byte cap 64KiB, single UTF-8 decode); client `ReadLine` under IO timeout (net48: `Task.Delay` race).

## Tests

| Test | net48 | net10 |
|------|-------|-------|
| `internet_pipe_guard_writes_nothing_until_full_request_line` | `InternetPipeServer` | line-framing stub (same write-after-read contract) |
| `internet_pipe_utf8_persian_field_round_trips_on_real_pipe` | `InternetPipeServer` | stub echo |

## Run ids

| Run | Head | Note |
|-----|------|------|
| **44606002155** | `5af88ae` | Literal evidence table above |
| **44598481278** | R18e | Prior `write_begin` hang reference |