# PR #40 R15 report (staging `cursor/pr40-r15-staging`)

Parent: `07e524f24cf33bb820197dc6c8e98a6b2ef8713d`  
Option A base: `7940b7dc6e5af43ce37f65e8a610a3ff0e7c58ac`

## Summary (Option A — persistent ActiveStore)

- **OFF/ON** apply on **ActiveStore** (no `-PolicyStore localhost` for product rules/profiles). Legacy **localhost** rules + profile Block cleared at **every apply** and at **`InternetPipeServer.Run` start** (`InternetFirewallLocalhostStoreCleanup`, `InternetGuardStartupFirewall.ReconcileOnPipeStart`).
- **Classifier** unchanged for server-TCP reachability; failures still `apply_failed` / `internetError`.
- **ON** after reset: `TryGetOnResetFailedReason` requires profiles not Block and **0** group rules on ActiveStore (else `apply_failed`).
- **State file:** `InternetGuardStateStore.SaveVerified` only after signed pipe apply succeeds; OFF pipe path re-classifies with `requireVerifiedStateFile: true` and rolls back to ON on failure.
- **L17:** `L17RealInternetOffProbe` uses real **Guard pipe** + heartbeat-signed command. Assertions unchanged: `blocked=True`, `rules>=1`, `state_present=True`; **added** `localhost` group rule count **= 0**.
- **Reboot (iii):** no hardware reboot on Linux agent; **`internet_guard_startup_clears_orphan_off_when_state_file_missing`** calls `InternetGuardStartupFirewall.ReconcileOnPipeStart` (same path as pipe start). Code: if ActiveStore shows OFF artifacts but **no** verified state file → `RunResetScript()` (unblock).

## Root cause (a) — R14 CI job 135509470398

1. `L17_GUARD_OFF_PROBE apply_result=applied blocked=False guard_rule_count=2 state_present=False`.
2. Before guard-wpf: ActiveStore profiles `NotConfigured`, `guard_rule_count=2` in localhost store, egress open.

**R14 bug:** rules/profiles on **localhost** only; verify matched localhost while ActiveStore stayed open. **Option A** moves product store to ActiveStore and keeps L17 rule count on ActiveStore.

## Root cause (c) — host timer / CI firewall

Same R14 localhost-only rules left **2** rules on runner before guard-wpf. **`Invoke-CiFirewallRestore`** before guard-wpf **retained**; applier no longer creates localhost product rules.

## Files (Option A)

- `InternetFirewallApplier.cs`, `InternetPowerShellRunner.cs`, `InternetFirewallLocalhostStoreCleanup.cs`, `InternetGuardStartupFirewall.cs`, `InternetFirewallOffApplyReadback.cs`, `InternetPipeServer.cs`, `InternetGuardCli.cs`
- `InternetFirewallOffVerification.cs` (+ on-reset helper)
- Tests: `L17RealInternetOffProbe.cs`, `WindowsPcInternetFirewallIntegrationTests.cs` (off/on, localhost cleanup, startup orphan), `WindowsPcInternetApplyFailureLiveTests.cs`, `PcInternetFirewallOffVerificationTests.cs`
- CI: existing `Category=PcInternetFirewall` + `PcSpecialBandLiveRateCardOracleTests` filters unchanged

## Staging SHA

*(set at push — see agent reply with `git rev-parse HEAD`)*

## L17 diagnostics

`L17_OFF_APPLY_DIAG` / probe line still print active vs localhost profile and rule counts for Windows CI triage.