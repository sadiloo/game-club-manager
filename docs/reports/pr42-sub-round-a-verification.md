# PR #42 / MVP sub-round A — verification map

Bindings follow `step-mvp-a-shell-plan-r1`. **WindowsShell** proofs run only in the `windows-2022 shell` job (`Category=WindowsShell`). Server loopback / operator proofs run on **ubuntu net10** (`GameClub.Server.Tests`).

## A-1..A-14

| ID | Proof |
|----|--------|
| **A-1** | `shell_first_run_enables_checkpoint_shell_run_value` |
| **A-2** | `shell_autostart_uncheck_removes_run_value` |
| **A-3** | `shell_events_do_not_change_open_session_amount_after_ten_minutes` |
| **A-4** | `shell_checkpoint_server_service_is_delayed_auto_start` |
| **A-5** | `shell_window_json_restores_size_position_maximized` |
| **A-6** | `shell_window_json_clamps_position_when_monitor_missing` |
| **A-7** | `shell_close_hide_exit_and_kill_leave_checkpoint_server_running` |
| **A-8** | `local_server_status_loopback_200_with_started_at_and_db_ok`, `local_server_status_returns_404_off_loopback`, `local_server_status_db_ok_false_when_database_unreachable` (ubuntu) |
| **A-9** | `open_sessions_count_includes_open_and_paused_only`, `open_sessions_returns_404_without_api_key_or_non_loopback` (ubuntu) |
| **A-10** | `shell_elevated_stop_with_zero_sessions_skips_warning` |
| **A-11** | `shell_elevated_stop_shows_open_session_count_warning` |
| **A-12** | `shell_elevated_stop_cancel_leaves_service_running` |
| **A-13** | `shell_tray_start_brings_service_to_running` |
| **A-14** | `shell_standard_user_cannot_stop_service` |

### Plan extras (same shell job, not separate A-rows)

| Binding | Proof |
|---------|--------|
| **S-2** (operator panel load) | `shell_navigates_to_operator_within_ten_seconds_after_service_start` |
| **S-3** (offline / navigation lockdown) | `shell_shows_persian_offline_when_service_stopped`, `shell_blocks_external_navigation_and_new_window_with_log_line`, `shell_page_alert_still_shows_webview_default_dialog` |
| **R-14** (WebView hardening) | `shell_webview_settings_disable_context_menu_devtools_and_browser_accelerators`, `shell_simulated_context_menu_and_f12_do_not_open_devtools`, `shell_ctrl_p_does_not_open_print_dialog`, `shell_zoom_keys_follow_point_one_steps`, `shell_blocked_reload_keys_do_not_raise_navigation`, `shell_tray_reload_causes_exactly_one_navigation` |

## F1..F14 (Ben tray status v2.2)

| ID | Proof |
|----|--------|
| **F1** | `shell_f1_stopped_red_start_stop_pending_yellow`; `shell_tray_icon_maps_stopped_start_pending_and_running_to_red_yellow_green_with_persian_tooltip` |
| **F2** | Green tray only when SCM running **and** loopback `dbOk` — `shell_tray_icon_maps_stopped_start_pending_and_running_to_red_yellow_green_with_persian_tooltip` |
| **F3** | Stopped vs no-answer wording — `shell_f5_running_without_db_ok_becomes_no_answer_red_with_distinct_text` |
| **F4** | `shell_f4_delayed_auto_boot_under_five_minutes_shows_yellow` |
| **F5** | `shell_f5_running_without_db_ok_becomes_no_answer_red_with_distinct_text`, `shell_f5_running_menu_shows_persian_yesterday_start_time` |
| **F6** | `shell_f6_unexpected_stop_toast_has_no_latin_letters` |
| **F7** | `shell_tray_menu_status_line_shows_tehran_start_time_when_running`, `shell_tray_menu_status_line_shows_stopped_when_service_stopped` |
| **F8** | `local_server_status_loopback_200_with_started_at_and_db_ok`, `local_server_status_db_ok_false_when_database_unreachable` (ubuntu) |
| **F9** | `open_sessions_count_includes_open_and_paused_only` (ubuntu) |
| **F10** | `shell_f10_maintenance_flag_suppresses_unexpected_stop_toast` |
| **F11** | `shell_unexpected_service_stop_shows_exactly_one_notification` |
| **F12** | `shell_tray_initiated_stop_shows_no_unexpected_stop_notification` |
| **F13** | `shell_disk_warning_flag_shows_persian_balloon_within_sixty_seconds` |
| **F14** | `server_status_returns_started_at_utc_and_last_backup_from_newest_file`, `operator_panel_server_status_chip_polls_and_shows_persian_lines` (ubuntu) |

## CI inventory markers

- **windows net48 and net10:** `SHELL_EXCLUDED_TEST` / `SHELL_EXCLUDED_COUNT` via `scripts/windows/shell-windowshell-inventory.ps1 -Role Excluded` (Category=WindowsShell list must match slnx subset).
- **windows-2022 shell:** `SHELL_LIST_TEST` / `SHELL_LIST_COUNT` via the same script `-Role Shell` before `Category=WindowsShell` test run.