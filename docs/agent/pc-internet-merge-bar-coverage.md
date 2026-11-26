# PC internet merge bar coverage (brief §10.3–10.4)

| Brief bullet | Test | File |
|---|---|---|
| Migration default surcharge → online night 100k/130k | `PcInternetMigration_FromPreState_DefaultSurcharge20000_OnlineNight100000_NoOfflinePcLines` | `PcInternetRatesMigrationTests.cs` |
| Migration surcharge 25k → 105k/135k | `PcInternetMigration_Surcharge25000_Night105000_135000` | same |
| No offline lines inserted | same + `PcInternetMigration_PcNormalOnlineDayRate_80000_no_surcharge_column` | same |
| PS5 rate lines unchanged | `PcInternetMigration_Ps5RateLinesUnchanged` | same |
| Game rate lines byte-identical | `PcInternetMigration_GameRateLinesByteIdentical` | same |
| Toggle operator/manager/owner + 401 | `PcInternetToggle_manager_and_owner_allowed`, `PcInternetToggle_Unauthenticated_401` | `PcInternetServerScenariosTests.cs`, `PcInternetMergeBarServerTests.cs` |
| Toggle `not_pc`, stale_version, idempotent, same-state | `PcInternetToggle_ConsoleSession_returns_not_pc`, `PcInternetToggle_stale_version`, `PcInternetToggle_WithOfflineRates_creates_segment`, `PcInternetToggle_SameState_NoOp` | `PcInternetToggleApiTests.cs`, `PcInternetServerScenariosTests.cs`, `PcInternetMergeBarServerTests.cs` |
| Toggle command/version required, closed, awaiting_payment, manual_settle, session_busy | `PcInternetToggle_*` | `PcInternetBrief103CompletionTests.cs` |
| `offline_rate_missing` no segment/audit | `PcInternetToggle_OfflineRateMissing_NoSegment_NoVersionBump` | `PcInternetMergeBarServerTests.cs` |
| Start Online (API + kiosk) | `PcStart_Ignores_ModeOffline_stays_Online`, `PcInternet_KioskMemberStart_UsesOnlineMode`, `PcInternet_KioskGuestStart_ForcesOnlineMode` | `PcInternetServerScenariosTests.cs`, `PcInternetMergeBarServerTests.cs`, `PcInternetBrief103CompletionTests.cs` |
| End paths `internet.auto_on` | `PcInternet_end_*_writes_internet_auto_on` (5) | `PcInternetBrief103CompletionTests.cs` |
| L13 offline pair, session before save, new session | `PcInternet_L13_*`, grid `internetToggleAllowed` | `PcInternetBrief103CompletionTests.cs`, `PcInternetToggleApiTests.cs`, `PcInternetMergeBarServerTests.cs` |
| L15 one more minute | `PcInternet_L15_amount_prepaid_grants_one_more_minute_below_new_rate` | `PcInternetBrief103CompletionTests.cs` |
| L17 apply_failed + toggle back | `PcInternet_L17_firewall_error_still_offline_segment_and_grid_then_toggle_back` | same |
| L18 duration → amount budget | `PcInternet_L18_duration_toggle_sets_amount_kind_and_budget` | same |
| L19 transfer conversion | `PcInternet_L19_transfer_converts_duration_without_internet_change`, oracle `E13` | `PcInternetBrief103CompletionTests.cs`, `PcInternetBillOracleTests.cs` |
| Heartbeat ack + applied/failed audit + kioskConnected | `PcInternet_Heartbeat_*`, `PcInternet_KioskConnected_false_after_15s` | `PcInternetBrief103CompletionTests.cs`, `PcInternetMergeBarServerTests.cs`, `PcInternetAddendumTests.cs` |
| Grid §7.1/§7.2 fields | `PcInternetGrid_*` | `PcInternetGridTests.cs` |
| PC rates API 403 + upsert | `PcRates_Operator_gets_403_persian_message`, rates migration/oracle | `PcInternetAddendumTests.cs` |
| Prepaid E6 lease / bill oracle E1–E13 | `PcInternet_Prepaid_E6_lease_*`, `PcInternet_Oracle_*` | `PcInternetBrief103CompletionTests.cs`, `PcInternetBillOracleTests.cs` |
| Transfer source ON | `PcInternet_Transfer_source_desired_on_audit_transfer_source` | `PcInternetBrief103CompletionTests.cs` |
| app.js chips/confirm | `PcInternetAppJsTests` | `PcInternetAppJsTests.cs` |
| Billing E1–E13 unit | `PcInternetBillingTests` | `GameClub.Billing.Tests` |
| Core verify + admission + state file | `PcInternet_*` | `PcInternetKioskCoreTests.cs` |
| Firewall OFF/ON, third-party rule, configure CLI | `internet_*` | `WindowsPcInternetFirewallIntegrationTests.cs` |
| Live server E2E (a,d,e) | `live_server_pc_internet_toggle_off_report_applied_then_session_end_on` | `WindowsKioskServerIntegrationTests.cs` |

## Not run on CI (Linux / policy)

| Brief item | Reason |
|---|---|
| Migration open-session bill parity (B1) before `PcInternetRates` | `PcInternetMigration_OpenPcSessionBillUnchanged_AfterUp` |
| Migration down/up round-trip | `PcInternetMigration_DownThenUp_round_trip_restores_pre_state_and_reapplies_up` |
| §10.4.7 (b)(c) server outage + kiosk kill + reconnect | `live_server_pc_internet_off_unaffected_by_server_outage`, `live_server_pc_internet_on_while_kiosk_killed_shows_pending_then_applied_on_reconnect`, `internet_off_egress_stays_blocked_when_checkpoint_server_process_killed` |
| §10.4.5 Guard service stop/start + drift 70s | `WindowsPcInternetGuardPersistenceIntegrationTests` (restart, drift, pipe replay) |
| §10.4.7 replay on pipe | Covered by `InternetCommandAdmission` (Core); full pipe replay on `windows-2022` optional |
| Real cafe network / reboot | Brief §10.5 |