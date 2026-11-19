# PR #40 R14 report (staging cursor/pr40-r14-staging)

Parent: dba1e26ebb0835ca0fe920b1cb28942bc7f09c01

## Summary

Guard OFF script: line-36 rule-group query treats **ObjectNotFound** only as empty (Stop elsewhere); allow rules before profile block; `$profilesBlocked = $true` before profile loop so partial block rolls back; profile rollback on verify failure. **Product** rollback tested via `InternetFirewallApplierTestSeams.ThrowAfterProfilesBeforeVerify` (internal, off by default). WPF night rate card uses `PcSpecialBandLiveRateCardHelperNet48Mirror`; net10 oracle tests prove mirror matches `PcSpecialBandHostArgs` / `PcBandHarnessDiagnostics` for night, day, production, and explicit args.

## CI lines unchanged

- `L17_GUARD_OFF_PROBE` assertions (blocked=True, rules>=1, state present)
- `LEASE_BUILD`, `LEASE_VERIFY`, funded-lease, `CI_EGRESS`, `L17_CI`

## Expected fixes

- L17 probe: `apply_result=applied` on first guard apply (no `CmdletizationQuery_NotFound_RuleGroup`).
- WPF night line: in-process estimator 1740s matches server-driven display.