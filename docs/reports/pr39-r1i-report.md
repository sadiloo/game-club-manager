# PR #39 R1i report

## Git
- **head:** `13244d7` (see `git rev-parse HEAD` after push)
- **parent:** `29c99969c0c78ec1db64a8bafd6681a06a68a8ac`
- **branch:** `cursor/kiosk-reconnect-b6`

## Files vs `29c99969`
(See `git diff --stat 29c99969` after commit.)

## Compile gate (local)
```text
export PATH="$HOME/.dotnet:/usr/local/bin:$PATH"
for p in tests/*/*.csproj; do scripts/agent/build.sh "$p" -c Release; done
dotnet build tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj \
  -c Release -f net48 -p:EnableWindowsTargeting=true -p:TreatWarningsAsErrors=false
```
All four test projects (net10) and net48 integration build: **0 errors** on this VM.

```text
=== net10 test projects ===
build ok: tests/GameClub.Billing.Tests/GameClub.Billing.Tests.csproj
build ok: tests/GameClub.Kiosk.Core.Tests/GameClub.Kiosk.Core.Tests.csproj
build ok: tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj
build ok: tests/GameClub.Server.Tests/GameClub.Server.Tests.csproj
=== net48 integration ===
    0 Warning(s)
    0 Error(s)
```

## Tests (net10, this VM)
- **GameClub.Kiosk.Core.Tests:** 69/69 pass
- **GameClub.Billing.Tests:** 204/204 pass
- **GameClub.Server.Tests:** blocked — PostgreSQL not installable on this image (`apt` has no `postgresql` package; no Docker). Not in PR scope.
- **Host WPF / net48 mutants:** require Windows CI (Linux `MUT_HOST_ONLY` baseline `RUN_ABORTED`).

## Mutants
### Core (local run — all killed)
| Mutant | Killing test(s) |
|--------|------------------|
| Md_backoff_reset_removed | hub_lifecycle_recovers_after_more_than_four_consecutive_start_failures_then_resets_backoff |
| Me_give_up_after_4_attempts | hub_lifecycle_handles_multiple_closed_events_and_restarts, … |
| Me1_give_up_after_1_attempt | (same family) |
| Mb_inner_enqueue_catch_removed | heartbeat_inner_enqueue_catch_keeps_loop_without_supervisor_restart |
| Ml_reverse_monotonic | client_monotonic_increases_across_two_lifecycle_reconnects, … |
| RePair_revert_begin_early_return | (broad reconnect suite) |
| M_begin_early_return_keeps_loop | start_async_twice_without_reset_keeps_reconnect_loop |
| M_hbfail_no_failure_increment | two_heartbeat_failures_without_stale_ack_show_no_server_response |
| M_srflag_no_autoreconnect_flag | connected_phase_with_signalr_auto_reconnecting_must_not_show_bare_connected |
| Mx_heartbeat_failures_never_reset | heartbeat_failure_counter_resets_after_successful_ack |
| Ms6_no_connected_on_signalr_reconnected | signalr_reconnected_publishes_connected_phase |
| P5_revert_publish_trycatch | connection_status_subscriber_throw_does_not_stop_reconnect_loop |
| P6_revert_double_dispose | EQUIVALENT |

### Host (Windows CI — patterns verified; kills expected on green baseline)
| Mutant | Target test |
|--------|-------------|
| Host_logout_cleanup_first | host_logout_shows_layout_when_session_cleanup_throws |
| Host_logout_http_before_lock | host_logout_open_ack_during_slow_post_stays_locked_without_session |
| Host_logout_no_guard_before_http | host_logout_open_ack_during_slow_post_stays_locked_without_session |
| Host_logout_no_post_await_relock | host_logout_open_ack_during_slow_post_stays_locked_without_session |
| (existing host-d5 mutants) | unchanged filters |

## Items 1–7
1. Logout guard before slow POST; re-lock + refresh after; failed POST Warning + clear guard for re-adopt. Tests added.
2. Refresh lock text after sid cleared (D6 test unchanged).
3. Mutations updated per brief.
4. `sawBareConnected` restored (≥ 51a4c7f) + final `no server response`.
5. Handover pid appends to `live-server.pid`.
6. Failed POST: main re-adopt behavior; `Trace.TraceWarning`.
7. Server f1 / clock: not touched.