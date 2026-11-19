# PR #40 R14 report (staging cursor/pr40-r14-staging)

Parent: dba1e26ebb0835ca0fe920b1cb28942bc7f09c01

## Summary

Guard OFF script: tolerate missing Checkpoint rule group on first query, create allow rules before blocking localhost profiles, roll back profiles on verify failure. WPF night remaining-time test builds rate card from the same harness band as live TestHost (`GAMECLUB_SERVER_HOST_ARGS` / `CHECKPOINT_SERVER_PC_BAND`). New firewall integration tests for empty-group first apply and verify-failure rollback.

## CI lines unchanged

- `L17_GUARD_OFF_PROBE` assertions (blocked=True, rules>=1, state present)
- `LEASE_BUILD`, `LEASE_VERIFY`, funded-lease, `CI_EGRESS`, `L17_CI`

## Expected fixes

- L17 probe: `apply_result=applied` on first guard apply (no `CmdletizationQuery_NotFound_RuleGroup`).
- WPF night line: in-process estimator 1740s matches server-driven display.