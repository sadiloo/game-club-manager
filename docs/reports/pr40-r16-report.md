# PR #40 R16 report (staging `cursor/pr40-r16-staging`)

Parent: `71114ceecd86ddb94e85b479b809baa8e850d6bc`

## (a) L17 seed FK — root cause

`L17RealInternetOffProbe.SeedStationInternetDesiredOffAsync` inserted `station_internet_state."StationId"` using the API **raw** id (e.g. `PC-FAIL-abc123`). The server stores **normalized** ids (`StationIdNormalizer`, uppercase letters). FK `FK_station_internet_state_stations_StationId` failed with Npgsql 23503.

**Fix (test-only):** resolve the real `stations."Id"` row before insert (raw + normalized lookup).

## (b) Host timer CI failures — root cause (not product)

`KioskHostService.cs` / `KioskHostStatusTimerTests.cs` **unchanged** in `dba1e26..71114ce`. Failure mode matches product rules: locked screen needs `consecutiveHeartbeatFailures >= 2` or stale ack (`KioskStationStatus`, lines 69–74).

**Introducing commit / line:** `07e524f` extended the windows integration job (guard firewall + band oracle path); `ci-integration.ps1` **single** `dotnet-test-net48-guard-wpf` vstest process (line ~608) runs all `WindowsWpf` + `WindowsServer` tests sequentially after net10 server + live-checkpoint + night WPF + server relaunches. Under loaded runner, heartbeat scenarios saw `hubInvokeCount=2` in 40s (5s default interval) — starvation/flakiness, not behavior change.

`4ad0b86` added `Invoke-CiFirewallRestore` (line 586); egress was already open before guard-wpf in R15 log — firewall was not the blocker.

**Fix (CI + test harness, assertions unchanged):**

1. Early `dotnet-test-net48-host-timer-fail` phase (fresh testhost) before bulk guard-wpf; exclude those four tests from the main guard filter.
2. `RunScenarioOnDispatcherAsync`: `TestHeartbeatInterval=200ms`, `TestDelayAsync=zero` (same pattern as `KioskReconnectCoreTests`).

## Staging push

Branch: `cursor/pr40-r16-staging` only.