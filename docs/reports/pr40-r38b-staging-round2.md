# PR #40 r38b staging — round 2 agent report

## Main billing proof after Down (B1/B2)

**Test:** `PcInternetRatesMigrationTests.PcInternetMigration_Down_main_legacy_deserialize_bills_without_duplicate_base_rate`

**Steps (real Postgres `MigrateAsync` / `IMigrator`, no C# snapshot twin):**

1. Reset DB → migrate latest → migrate down to `20261117041656_Ss14FixR1` (pre–PC-internet).
2. Set `branch_rounding.PcSpecialBandSurchargeToman = 25000`.
3. Seed open PC session `PC-MAIN` with legacy snapshot from DB rate lines (80k/105k offline day/night).
4. Edit global offline day to 90k; **Up** (`PcInternetRates`).
5. Edit global online day/night to 90k/130k (40k global surcharge).
6. **Down** to pre migration via `IMigrator.Migrate(Pre)`.
7. Read open-session `rate_card_snapshot.Json` from Postgres.

**Assertions (outputs on green run):**

| Check | Expected |
|-------|----------|
| Reverted snapshot has no `SchemaVersion: 2` | pass |
| `PcSpecialBandSurchargeToman` on snapshot | **25000** (session own, not 40k global) |
| Offline/1 day lines for `pc-normal` | **1** (no duplicate base) |
| `RateConfigService.DeserializeSnapshot(json)` | no `Duplicate base rate` |
| Bill 10:00–11:00 | **80000** |
| Bill 10:00–11:30 | **120000** |
| `branch_rounding.PcSpecialBandSurchargeToman` after Down | **25000** |

**Main-path billing:** `DeserializeSnapshot` + `SessionBilling.BuildChargeAsync` use the same legacy snapshot path as main (`977703d`) for v1 JSON (`BuildCardLegacy` in `RateConfigService`). No worktree required; Down leaves v1 JSON in DB and the unchanged legacy deserializer prices it.

## Other gate items

- **B3:** `PcInternetMigration_MigratedOpenSession_toggleOff_returns_offline_rate_missing` → 409 `offline_rate_missing`, 0 global offline rate lines.
- **Snapshots:** `PcInternetSnapshotSupport.cs` removed; `OpenSession_afterUp_sql_snapshot_*` reads JSON after migration SQL.
- **B4:** `KioskShellOrchestrator` passes `(true,"ok")` into HB ack logging after a single successful `VerifyLease` (no second `TryVerify` on Playing).

## Tests run (agent)

- `PcInternetRatesMigrationTests` 13/13
- `pc-rates-shipped-parser-selftest.mjs` ok