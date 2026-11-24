using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class PcInternetRatesMigrationTests(GameClubWebApplicationFactory factory)
{
    private const string PrePcInternetMigration = "20261117041656_Ss14FixR1";

    private static async Task EnsureLatestMigrationAsync(AppDbContext db)
    {
        await db.Database.MigrateAsync();
    }

    private static async Task EnsurePrePcInternetMigrationAsync(AppDbContext db)
    {
        await db.Database.MigrateAsync();
        db.GetService<IMigrator>().Migrate(PrePcInternetMigration);
        await DatabaseSeed.EnsureAsync(db);
    }

    private static async Task WithPrePcInternetSeedAsync(GameClubWebApplicationFactory factory, Func<AppDbContext, Task> body)
    {
        await S3TestHelpers.ResetAsync(factory);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            await EnsurePrePcInternetMigrationAsync(db);
            await body(db);
        }
        finally
        {
            await EnsureLatestMigrationAsync(db);
        }
    }

    [Fact]
    public async Task PcInternetMigration_PcNormalOnlineDayRate_80000_no_surcharge_column()
    {
        await S3TestHelpers.ResetAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var line = await db.RateLines.SingleAsync(r =>
            r.GroupId == PilotSeed.PcNormalGroupId
            && r.PlayMode == nameof(PlayMode.Online)
            && r.PlayerCount == null
            && !r.IsSpecialBand);
        Assert.Equal(80_000, line.HourlyToman);
        var hasSurcharge = await db.Database.SqlQueryRaw<int>(
            """
            SELECT COUNT(*)::int AS "Value"
            FROM information_schema.columns
            WHERE table_name = 'branch_rounding' AND column_name = 'PcSpecialBandSurchargeToman'
            """).SingleAsync();
        Assert.Equal(0, hasSurcharge);
    }

    [Fact]
    public async Task PcInternetMigration_FromPreState_DefaultSurcharge20000_OnlineNight100000_NoOfflinePcLines()
    {
        await WithPrePcInternetSeedAsync(factory, async db =>
        {
            await db.Database.MigrateAsync();
            Assert.False(await db.RateLines.AnyAsync(r =>
                r.GroupId == PilotSeed.PcNormalGroupId && r.PlayMode == nameof(PlayMode.Offline)));
            var day = await db.RateLines.SingleAsync(r =>
                r.GroupId == PilotSeed.PcNormalGroupId && r.PlayMode == nameof(PlayMode.Online) && !r.IsSpecialBand);
            var night = await db.RateLines.SingleAsync(r =>
                r.GroupId == PilotSeed.PcNormalGroupId && r.PlayMode == nameof(PlayMode.Online) && r.IsSpecialBand);
            Assert.Equal(80_000, day.HourlyToman);
            Assert.Equal(100_000, night.HourlyToman);
        });
    }

    [Fact]
    public async Task PcInternetMigration_Surcharge25000_Night105000_135000()
    {
        await WithPrePcInternetSeedAsync(factory, async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""UPDATE branch_rounding SET "PcSpecialBandSurchargeToman" = 25000""");
            await db.Database.MigrateAsync();
            var normalNight = await db.RateLines.SingleAsync(r =>
                r.GroupId == PilotSeed.PcNormalGroupId && r.IsSpecialBand && r.PlayMode == nameof(PlayMode.Online));
            var vipNight = await db.RateLines.SingleAsync(r =>
                r.GroupId == PilotSeed.PcVipGroupId && r.IsSpecialBand && r.PlayMode == nameof(PlayMode.Online));
            Assert.Equal(105_000, normalNight.HourlyToman);
            Assert.Equal(135_000, vipNight.HourlyToman);
        });
    }

    [Fact]
    public async Task PcInternetMigration_Ps5RateLinesUnchanged()
    {
        await WithPrePcInternetSeedAsync(factory, async db =>
        {
            var ps5Before = await db.RateLines.AsNoTracking()
                .Where(r => r.GroupId == GameTimerReference.Ps5GroupId)
                .OrderBy(r => r.PlayMode).ThenBy(r => r.PlayerCount).ThenBy(r => r.IsSpecialBand)
                .Select(r => $"{r.PlayMode}:{r.PlayerCount}:{r.IsSpecialBand}:{r.HourlyToman}")
                .ToListAsync();
            await db.Database.MigrateAsync();
            var ps5After = await db.RateLines.AsNoTracking()
                .Where(r => r.GroupId == GameTimerReference.Ps5GroupId)
                .OrderBy(r => r.PlayMode).ThenBy(r => r.PlayerCount).ThenBy(r => r.IsSpecialBand)
                .Select(r => $"{r.PlayMode}:{r.PlayerCount}:{r.IsSpecialBand}:{r.HourlyToman}")
                .ToListAsync();
            Assert.Equal(ps5Before, ps5After);
        });
    }

    [Fact]
    public async Task PcInternetMigration_GameRateLinesByteIdentical()
    {
        await WithPrePcInternetSeedAsync(factory, async db =>
        {
            var before = await db.GameRateLines.AsNoTracking().OrderBy(g => g.Id)
                .Select(g => $"{g.GameTitleId}:{g.PlayMode}:{g.PlayerCount}:{g.HourlyToman}:{g.NightHourlyToman}")
                .ToListAsync();
            await db.Database.MigrateAsync();
            var after = await db.GameRateLines.AsNoTracking().OrderBy(g => g.Id)
                .Select(g => $"{g.GameTitleId}:{g.PlayMode}:{g.PlayerCount}:{g.HourlyToman}:{g.NightHourlyToman}")
                .ToListAsync();
            Assert.Equal(before, after);
        });
    }

    [Fact]
    public async Task PcInternetMigration_OpenPcSessionBillUnchanged_AfterUp()
    {
        await WithPrePcInternetSeedAsync(factory, async db =>
        {
            var api = factory.CreateClientWithKey();
            await S3TestHelpers.RegisterPc(api, "PC-MIG-OPEN", PilotSeed.PcNormalGroupId);
            await S3TestHelpers.RegisterPc(api, "PC-MIG-CLOSED", PilotSeed.PcNormalGroupId);
            var (openSessionId, endUtc, expectedToman) = await SeedPreMigrationPcSessionsAsync(db, "PC-MIG-OPEN", "PC-MIG-CLOSED", skipStations: true);
            var before = await ChargeTotalAsync(factory.Services, openSessionId, endUtc);
            Assert.Equal(expectedToman, before);
            await db.Database.MigrateAsync();
            var after = await ChargeTotalAsync(factory.Services, openSessionId, endUtc);
            Assert.Equal(expectedToman, after);
            var session = await db.Sessions.AsNoTracking().Include(s => s.Segments).SingleAsync(s => s.Id == openSessionId);
            Assert.Equal(PlayMode.Online, session.Mode);
            Assert.All(session.Segments, s => Assert.Equal(PlayMode.Online, s.Mode));
        });
    }

    [Fact]
    public async Task PcInternetMigration_ClosedLegacySnapshotRepricesIdentically()
    {
        await WithPrePcInternetSeedAsync(factory, async db =>
        {
            var api = factory.CreateClientWithKey();
            await S3TestHelpers.RegisterPc(api, "PC-MIG-OPEN", PilotSeed.PcNormalGroupId);
            await S3TestHelpers.RegisterPc(api, "PC-MIG-CLOSED", PilotSeed.PcNormalGroupId);
            var (_, endUtc, expectedToman) = await SeedPreMigrationPcSessionsAsync(db, "PC-MIG-OPEN", "PC-MIG-CLOSED", skipStations: true);
            var closedId = await db.Sessions.AsNoTracking().Where(s => s.StationId == "PC-MIG-CLOSED").Select(s => s.Id).SingleAsync();
            var before = await ChargeTotalAsync(factory.Services, closedId, endUtc);
            Assert.Equal(expectedToman, before);
            await db.Database.MigrateAsync();
            var after = await ChargeTotalAsync(factory.Services, closedId, endUtc);
            Assert.Equal(expectedToman, after);
        });
    }

    [Fact]
    public async Task PcInternetMigration_OpenSessionBillUnchanged_when_rates_edited_mid_session_before_up()
    {
        await WithPrePcInternetSeedAsync(factory, async db =>
        {
            var start = TestTimes.T(2026, 6, 2, 10, 0).UtcDateTime;
            var endUtc = TestTimes.T(2026, 6, 2, 11, 0).UtcDateTime;
            var sessionId = await SeedOpenPreMigrationPcSessionAsync(db, "PC-MIG-R8B", start, endUtc);
            var beforeEdit = await ChargeTotalAsync(factory.Services, sessionId, endUtc);
            Assert.Equal(80_000, beforeEdit);
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE rate_lines SET "HourlyToman" = 90000
                WHERE "GroupId"='pc-normal' AND "PlayMode"='Offline' AND "PlayerCount"=1 AND NOT "IsSpecialBand"
                """);
            var afterRateEdit = await ChargeTotalAsync(factory.Services, sessionId, endUtc);
            Assert.Equal(80_000, afterRateEdit);
            await db.Database.MigrateAsync();
            var afterUp = await ChargeTotalAsync(factory.Services, sessionId, endUtc);
            Assert.Equal(80_000, afterUp);
            var stopUtc = TestTimes.T(2026, 6, 2, 11, 30).UtcDateTime;
            var stopBill = await ChargeTotalAsync(factory.Services, sessionId, stopUtc);
            Assert.Equal(120_000, stopBill);
        });
    }

    [Fact]
    public async Task PcInternetMigration_DownThenUp_round_trip_restores_pre_state_and_reapplies_up()
    {
        await S3TestHelpers.ResetAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await EnsurePrePcInternetMigrationAsync(db);
        var surchargeBefore = await db.Database.SqlQueryRaw<long>(
            """SELECT "PcSpecialBandSurchargeToman" AS "Value" FROM branch_rounding LIMIT 1""").SingleAsync();
        Assert.Equal(20_000, surchargeBefore);
        await db.Database.MigrateAsync();
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
            """
            SELECT COUNT(*)::int AS "Value"
            FROM information_schema.columns
            WHERE table_name = 'branch_rounding' AND column_name = 'PcSpecialBandSurchargeToman'
            """).SingleAsync());
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate(PrePcInternetMigration);
        surchargeBefore = await db.Database.SqlQueryRaw<long>(
            """SELECT "PcSpecialBandSurchargeToman" AS "Value" FROM branch_rounding LIMIT 1""").SingleAsync();
        Assert.Equal(20_000, surchargeBefore);
        await db.Database.MigrateAsync();
        Assert.Equal(80_000, await db.RateLines.Where(r =>
                r.GroupId == PilotSeed.PcNormalGroupId && r.PlayMode == nameof(PlayMode.Online) && !r.IsSpecialBand)
            .Select(r => r.HourlyToman).SingleAsync());
    }

    [Fact]
    public async Task PcInternetMigration_DownThenUp_round_trip_with_edited_rates_and_open_session_preserves_surcharge_and_bill()
    {
        await S3TestHelpers.ResetAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await EnsurePrePcInternetMigrationAsync(db);
        await db.Database.ExecuteSqlRawAsync("""UPDATE branch_rounding SET "PcSpecialBandSurchargeToman" = 25000""");
        var start = TestTimes.T(2026, 6, 2, 10, 0).UtcDateTime;
        var checkpoint = TestTimes.T(2026, 6, 2, 11, 0).UtcDateTime;
        var sessionId = await SeedOpenPreMigrationPcSessionAsync(db, "PC-RT", start, checkpoint);
        await db.Database.ExecuteSqlRawAsync(
            """UPDATE rate_lines SET "HourlyToman" = 90000 WHERE "GroupId"='pc-normal' AND "PlayMode"='Offline' AND "PlayerCount"=1 AND NOT "IsSpecialBand" """);

        await db.Database.MigrateAsync();
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate(PrePcInternetMigration);
        var surcharge = await db.Database.SqlQueryRaw<long>(
            """SELECT "PcSpecialBandSurchargeToman" AS "Value" FROM branch_rounding LIMIT 1""").SingleAsync();
        Assert.Equal(25_000, surcharge);
        var stationMode = await db.Stations.Where(s => s.Id == "PC-RT").Select(s => s.DefaultPlayMode).SingleAsync();
        Assert.Equal(PlayMode.Offline, stationMode);
        await db.Database.MigrateAsync();
        var night = await db.RateLines.SingleAsync(r =>
            r.GroupId == PilotSeed.PcNormalGroupId && r.IsSpecialBand && r.PlayMode == nameof(PlayMode.Online));
        Assert.Equal(115_000, night.HourlyToman);
        var at11 = TestTimes.T(2026, 6, 2, 11, 0).UtcDateTime;
        Assert.Equal(80_000, await ChargeTotalAsync(factory.Services, sessionId, at11));
    }

    [Fact]
    public async Task PcInternetMigration_OpenSession_afterUp_sql_snapshot_has_online_day_not_offline_day_duplicate()
    {
        await WithPrePcInternetSeedAsync(factory, async db =>
        {
            var start = TestTimes.T(2026, 6, 2, 10, 0).UtcDateTime;
            var checkpoint = TestTimes.T(2026, 6, 2, 11, 0).UtcDateTime;
            var sessionId = await SeedOpenPreMigrationPcSessionAsync(db, "PC-V2", start, checkpoint);
            await db.Database.MigrateAsync();
            var json = await db.Sessions.AsNoTracking()
                .Where(s => s.Id == sessionId)
                .Join(db.RateCardSnapshots, s => s.RateCardSnapshotId, r => r.Id, (_, r) => r.Json)
                .SingleAsync();
            AssertV2OpenSnapshotWithoutOfflineDayBase(json);
        });
    }

    [Fact]
    public async Task PcInternetMigration_Down_reverted_session_snapshot_deserialize_bills_without_duplicate_base_rate()
    {
        try
        {
        await S3TestHelpers.ResetAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await EnsurePrePcInternetMigrationAsync(db);
        await db.Database.ExecuteSqlRawAsync("""UPDATE branch_rounding SET "PcSpecialBandSurchargeToman" = 25000""");
        var start = TestTimes.T(2026, 6, 2, 10, 0).UtcDateTime;
        var checkpoint = TestTimes.T(2026, 6, 2, 11, 0).UtcDateTime;
        var sessionId = await SeedOpenPreMigrationPcSessionAsync(db, "PC-MAIN", start, checkpoint);
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE rate_lines SET "HourlyToman" = 90000
            WHERE "GroupId"='pc-normal' AND "PlayMode"='Offline' AND "PlayerCount"=1 AND NOT "IsSpecialBand"
            """);
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE rate_lines SET "HourlyToman" = 90000
            WHERE "GroupId"='pc-normal' AND "PlayMode"='Online' AND "PlayerCount" IS NULL AND NOT "IsSpecialBand";
            UPDATE rate_lines SET "HourlyToman" = 130000
            WHERE "GroupId"='pc-normal' AND "PlayMode"='Online' AND "PlayerCount" IS NULL AND "IsSpecialBand";
            """);
        db.GetService<IMigrator>().Migrate(PrePcInternetMigration);

        var json = await db.Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Join(db.RateCardSnapshots, s => s.RateCardSnapshotId, r => r.Id, (_, r) => r.Json)
            .SingleAsync();
        using (var doc = JsonDocument.Parse(json))
        {
            Assert.False(doc.RootElement.TryGetProperty("SchemaVersion", out var sv) && sv.GetInt32() == 2);
            Assert.Equal(25_000, doc.RootElement.GetProperty("PcSpecialBandSurchargeToman").GetInt64());
            var offlineDayCount = doc.RootElement.GetProperty("Lines").EnumerateArray().Count(l =>
                l.GetProperty("GroupId").GetString() == PilotSeed.PcNormalGroupId
                && l.GetProperty("PlayMode").GetString() == nameof(PlayMode.Offline)
                && l.TryGetProperty("PlayerCount", out var pc) && pc.GetInt32() == 1
                && !l.GetProperty("IsSpecialBand").GetBoolean());
            Assert.Equal(1, offlineDayCount);
        }

        await using var billScope = factory.Services.CreateAsyncScope();
        var rates = billScope.ServiceProvider.GetRequiredService<RateConfigService>();
        var _ = rates.DeserializeSnapshot(json);

        var at11 = TestTimes.T(2026, 6, 2, 11, 0).UtcDateTime;
        Assert.Equal(80_000, await ChargeTotalAsync(factory.Services, sessionId, at11));
        var stopUtc = TestTimes.T(2026, 6, 2, 11, 30).UtcDateTime;
        Assert.Equal(120_000, await ChargeTotalAsync(factory.Services, sessionId, stopUtc));
        var surcharge = await db.Database.SqlQueryRaw<long>(
            """SELECT "PcSpecialBandSurchargeToman" AS "Value" FROM branch_rounding LIMIT 1""").SingleAsync();
        Assert.Equal(40_000, surcharge);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await EnsureLatestMigrationAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }
    }

    [Fact]
    public async Task PcInternetMigration_DownThenUp_without_open_sessions_restores_manager_edited_night_rates()
    {
        try
        {
            await S3TestHelpers.ResetAsync(factory);
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsurePrePcInternetMigrationAsync(db);
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE rate_lines SET "HourlyToman" = 90000
                WHERE "GroupId"='pc-normal' AND "PlayMode"='Online' AND "PlayerCount" IS NULL AND NOT "IsSpecialBand";
                UPDATE rate_lines SET "HourlyToman" = 115000
                WHERE "GroupId"='pc-normal' AND "PlayMode"='Online' AND "PlayerCount" IS NULL AND "IsSpecialBand";
                UPDATE rate_lines SET "HourlyToman" = 120000
                WHERE "GroupId"='pc-vip' AND "PlayMode"='Online' AND "PlayerCount" IS NULL AND NOT "IsSpecialBand";
                UPDATE rate_lines SET "HourlyToman" = 145000
                WHERE "GroupId"='pc-vip' AND "PlayMode"='Online' AND "PlayerCount" IS NULL AND "IsSpecialBand";
                """);
            db.GetService<IMigrator>().Migrate(PrePcInternetMigration);
            var preSurcharge = await db.Database.SqlQueryRaw<long>(
                """SELECT "PcSpecialBandSurchargeToman" AS "Value" FROM branch_rounding LIMIT 1""").SingleAsync();
            Assert.Equal(25_000, preSurcharge);
            await db.Database.MigrateAsync();
            var normalNight = await db.RateLines.SingleAsync(r =>
                r.GroupId == PilotSeed.PcNormalGroupId && r.IsSpecialBand && r.PlayMode == nameof(PlayMode.Online));
            var vipNight = await db.RateLines.SingleAsync(r =>
                r.GroupId == PilotSeed.PcVipGroupId && r.IsSpecialBand && r.PlayMode == nameof(PlayMode.Online));
            Assert.Equal(115_000, normalNight.HourlyToman);
            Assert.Equal(145_000, vipNight.HourlyToman);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await EnsureLatestMigrationAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }
    }

    [Fact]
    public async Task PcInternetMigration_DownThenUp_lossy_branch_surcharge_on_reUp_vip_night_160k()
    {
        try
        {
            await S3TestHelpers.ResetAsync(factory);
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsurePrePcInternetMigrationAsync(db);
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE rate_lines SET "HourlyToman" = 90000
                WHERE "GroupId"='pc-normal' AND "PlayMode"='Online' AND "PlayerCount" IS NULL AND NOT "IsSpecialBand";
                UPDATE rate_lines SET "HourlyToman" = 130000
                WHERE "GroupId"='pc-normal' AND "PlayMode"='Online' AND "PlayerCount" IS NULL AND "IsSpecialBand";
                UPDATE rate_lines SET "HourlyToman" = 120000
                WHERE "GroupId"='pc-vip' AND "PlayMode"='Online' AND "PlayerCount" IS NULL AND NOT "IsSpecialBand";
                UPDATE rate_lines SET "HourlyToman" = 180000
                WHERE "GroupId"='pc-vip' AND "PlayMode"='Online' AND "PlayerCount" IS NULL AND "IsSpecialBand";
                """);
            db.GetService<IMigrator>().Migrate(PrePcInternetMigration);
            const long pcNormalNightDayGap = 130_000 - 90_000;
            var preSurcharge = await db.Database.SqlQueryRaw<long>(
                """SELECT "PcSpecialBandSurchargeToman" AS "Value" FROM branch_rounding LIMIT 1""").SingleAsync();
            Assert.Equal(pcNormalNightDayGap, preSurcharge);
            await db.Database.MigrateAsync();
            var vipNight = await db.RateLines.SingleAsync(r =>
                r.GroupId == PilotSeed.PcVipGroupId && r.IsSpecialBand && r.PlayMode == nameof(PlayMode.Online));
            Assert.Equal(160_000, vipNight.HourlyToman);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await EnsureLatestMigrationAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }
    }

    [Fact]
    public async Task L1_Down_snapshot_revert_uses_pc_normal_gap_not_vip_max()
    {
        try
        {
            await S3TestHelpers.ResetAsync(factory);
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsurePrePcInternetMigrationAsync(db);
            var start = TestTimes.T(2026, 6, 2, 10, 0).UtcDateTime;
            var checkpoint = TestTimes.T(2026, 6, 2, 11, 0).UtcDateTime;
            var sessionId = await SeedOpenPreMigrationPcSessionAsync(db, "PC-L1-SNAP", start, checkpoint);
            await db.Database.MigrateAsync();
            var v2Json = await SessionSnapshotJsonAsync(db, sessionId);
            var patched = SetGroupOnlineDayNightInSnapshot(v2Json, PilotSeed.PcNormalGroupId, 90_000, 130_000);
            patched = SetGroupOnlineDayNightInSnapshot(patched, PilotSeed.PcVipGroupId, 120_000, 180_000);
            await ReplaceSessionSnapshotJsonAsync(db, sessionId, patched);
            db.GetService<IMigrator>().Migrate(PrePcInternetMigration);
            var revertedJson = await SessionSnapshotJsonAsync(db, sessionId);
            using var doc = JsonDocument.Parse(revertedJson);
            Assert.False(doc.RootElement.TryGetProperty("SchemaVersion", out _));
            const long pcNormalGap = 130_000 - 90_000;
            Assert.Equal(pcNormalGap, doc.RootElement.GetProperty("PcSpecialBandSurchargeToman").GetInt64());
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await EnsureLatestMigrationAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }
    }

    [Fact]
    public async Task Qa_Down_revert_snapshot_limit1_ordering_picks_lowest_day_rate_pair()
    {
        try
        {
            await S3TestHelpers.ResetAsync(factory);
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsurePrePcInternetMigrationAsync(db);
            var start = TestTimes.T(2026, 6, 2, 10, 0).UtcDateTime;
            var checkpoint = TestTimes.T(2026, 6, 2, 11, 0).UtcDateTime;
            var sessionId = await SeedOpenPreMigrationPcSessionAsync(db, "PC-QA-ORD", start, checkpoint);
            await db.Database.MigrateAsync();
            var v2Json = await SessionSnapshotJsonAsync(db, sessionId);
            var patched = AddDecoyPcNormalOnlinePairInSnapshot(v2Json, decoyDay: 70_000, decoyNight: 200_000);
            await ReplaceSessionSnapshotJsonAsync(db, sessionId, patched);
            db.GetService<IMigrator>().Migrate(PrePcInternetMigration);
            var revertedJson = await SessionSnapshotJsonAsync(db, sessionId);
            using var doc = JsonDocument.Parse(revertedJson);
            const long seedGap = 100_000 - 80_000;
            Assert.Equal(seedGap, doc.RootElement.GetProperty("PcSpecialBandSurchargeToman").GetInt64());
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await EnsureLatestMigrationAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }
    }

    [Fact]
    public async Task PcInternetMigration_DownThenUp_open_session_snapshot_priced_lines_match_after_round_trip()
    {
        try
        {
            await S3TestHelpers.ResetAsync(factory);
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsurePrePcInternetMigrationAsync(db);
            var start = TestTimes.T(2026, 6, 2, 10, 0).UtcDateTime;
            var checkpoint = TestTimes.T(2026, 6, 2, 11, 0).UtcDateTime;
            var sessionId = await SeedOpenPreMigrationPcSessionAsync(db, "PC-RT-SNAP", start, checkpoint);
            await db.Database.MigrateAsync();
            var jsonOriginalV2 = await SessionSnapshotJsonAsync(db, sessionId);
            var pricedBefore = PricedLinesFromSnapshot(jsonOriginalV2, pcGroupsOnly: true);
            db.GetService<IMigrator>().Migrate(PrePcInternetMigration);
            await db.Database.MigrateAsync();
            var jsonAfterRoundTrip = await SessionSnapshotJsonAsync(db, sessionId);
            var pricedAfter = PricedLinesFromSnapshot(jsonAfterRoundTrip, pcGroupsOnly: true);
            Assert.Equal(pricedBefore, pricedAfter);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await EnsureLatestMigrationAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }
    }

    [Fact]
    public async Task PcInternetMigration_DownThenUp_manager_night_with_open_session_preserves_snapshot_and_global_night()
    {
        try
        {
            await S3TestHelpers.ResetAsync(factory);
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await EnsurePrePcInternetMigrationAsync(db);
            var start = TestTimes.T(2026, 6, 2, 10, 0).UtcDateTime;
            var checkpoint = TestTimes.T(2026, 6, 2, 11, 0).UtcDateTime;
            var sessionId = await SeedOpenPreMigrationPcSessionAsync(db, "PC-MGR", start, checkpoint);
            await db.Database.MigrateAsync();
            var jsonBefore = await SessionSnapshotJsonAsync(db, sessionId);
            using (var doc = JsonDocument.Parse(jsonBefore))
            {
                var day = doc.RootElement.GetProperty("Lines").EnumerateArray().Single(l =>
                    l.GetProperty("GroupId").GetString() == PilotSeed.PcNormalGroupId
                    && l.GetProperty("PlayMode").GetString() == nameof(PlayMode.Online)
                    && !l.GetProperty("IsSpecialBand").GetBoolean());
                Assert.Equal(80_000, day.GetProperty("HourlyToman").GetInt64());
            }

            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE rate_lines SET "HourlyToman" = 130000
                WHERE "GroupId"='pc-normal' AND "PlayMode"='Online' AND "PlayerCount" IS NULL AND "IsSpecialBand";
                """);
            db.GetService<IMigrator>().Migrate(PrePcInternetMigration);
            await db.Database.MigrateAsync();
            var night = await db.RateLines.SingleAsync(r =>
                r.GroupId == PilotSeed.PcNormalGroupId && r.IsSpecialBand && r.PlayMode == nameof(PlayMode.Online));
            Assert.Equal(130_000, night.HourlyToman);
            var jsonAfter = await SessionSnapshotJsonAsync(db, sessionId);
            using (var doc = JsonDocument.Parse(jsonAfter))
            {
                var day = doc.RootElement.GetProperty("Lines").EnumerateArray().Single(l =>
                    l.GetProperty("GroupId").GetString() == PilotSeed.PcNormalGroupId
                    && l.GetProperty("PlayMode").GetString() == nameof(PlayMode.Online)
                    && !l.GetProperty("IsSpecialBand").GetBoolean());
                Assert.Equal(80_000, day.GetProperty("HourlyToman").GetInt64());
            }
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await EnsureLatestMigrationAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }
    }

    [Fact]
    public async Task PcInternetMigration_MigratedOpenSession_toggleOff_returns_offline_rate_missing()
    {
        await S3TestHelpers.ResetAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await EnsurePrePcInternetMigrationAsync(db);
        var start = TestTimes.T(2026, 6, 2, 10, 0).UtcDateTime;
        var checkpoint = TestTimes.T(2026, 6, 2, 11, 0).UtcDateTime;
        var sessionId = await SeedOpenPreMigrationPcSessionAsync(db, "PC-B3", start, checkpoint);
        await db.Database.MigrateAsync();
        Assert.False(await db.RateLines.AnyAsync(r =>
            r.GroupId == PilotSeed.PcNormalGroupId && r.PlayMode == nameof(PlayMode.Offline)));
        Assert.Equal(PlayMode.Online, await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.Mode).SingleAsync());
        var op = await factory.CreateOperatorClientAsync();
        var version = await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
        var res = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = version,
            commandId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("offline_rate_missing", body.GetProperty("code").GetString());
    }

    private static Task<string> SessionSnapshotJsonAsync(AppDbContext db, Guid sessionId)
        => db.Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Join(db.RateCardSnapshots, s => s.RateCardSnapshotId, r => r.Id, (_, r) => r.Json)
            .SingleAsync();

    private static async Task ReplaceSessionSnapshotJsonAsync(AppDbContext db, Guid sessionId, string json)
    {
        var snapId = await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.RateCardSnapshotId).SingleAsync();
        await db.Database.ExecuteSqlRawAsync(
            """UPDATE rate_card_snapshot SET "Json" = {0} WHERE "Id" = {1}""",
            json,
            snapId);
    }

    private static string SetGroupOnlineDayNightInSnapshot(string json, string groupId, long day, long night)
    {
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("snapshot json");
        foreach (var line in node["Lines"]!.AsArray())
        {
            if (line is null)
            {
                continue;
            }

            if (line["GroupId"]!.GetValue<string>() != groupId)
            {
                continue;
            }

            if (line["PlayMode"]!.GetValue<string>() != nameof(PlayMode.Online))
            {
                continue;
            }

            if (line["PlayerCount"] is JsonValue pc && pc.TryGetValue<int>(out _))
            {
                continue;
            }

            line["HourlyToman"] = line["IsSpecialBand"]!.GetValue<bool>() ? night : day;
        }

        return node.ToJsonString();
    }

    private static string AddDecoyPcNormalOnlinePairInSnapshot(string json, long decoyDay, long decoyNight)
    {
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("snapshot json");
        var lines = node["Lines"]!.AsArray();
        lines.Add(new JsonObject
        {
            ["GroupId"] = PilotSeed.PcNormalGroupId,
            ["PlayMode"] = nameof(PlayMode.Online),
            ["PlayerCount"] = null,
            ["IsSpecialBand"] = false,
            ["HourlyToman"] = decoyDay,
        });
        lines.Add(new JsonObject
        {
            ["GroupId"] = PilotSeed.PcNormalGroupId,
            ["PlayMode"] = nameof(PlayMode.Online),
            ["PlayerCount"] = null,
            ["IsSpecialBand"] = true,
            ["HourlyToman"] = decoyNight,
        });
        return node.ToJsonString();
    }

    private static HashSet<string> PricedLinesFromSnapshot(string json, bool pcGroupsOnly = false)
    {
        using var doc = JsonDocument.Parse(json);
        var lines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in doc.RootElement.GetProperty("Lines").EnumerateArray())
        {
            var gid = line.GetProperty("GroupId").GetString()!;
            if (pcGroupsOnly && gid is not (PilotSeed.PcNormalGroupId or PilotSeed.PcVipGroupId))
            {
                continue;
            }

            var mode = line.GetProperty("PlayMode").GetString()!;
            var special = line.GetProperty("IsSpecialBand").GetBoolean();
            var hourly = line.GetProperty("HourlyToman").GetInt64();
            var player = line.TryGetProperty("PlayerCount", out var pc) && pc.ValueKind != JsonValueKind.Null
                ? pc.GetInt32().ToString()
                : "-";
            lines.Add($"{gid}|{mode}|{player}|{special}|{hourly}");
        }

        return lines;
    }

    private static void AssertV2OpenSnapshotWithoutOfflineDayBase(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("SchemaVersion").GetInt32());
        foreach (var line in doc.RootElement.GetProperty("Lines").EnumerateArray())
        {
            var gid = line.GetProperty("GroupId").GetString();
            if (gid is not (PilotSeed.PcNormalGroupId or PilotSeed.PcVipGroupId))
            {
                continue;
            }

            var mode = line.GetProperty("PlayMode").GetString();
            var isSpecial = line.GetProperty("IsSpecialBand").GetBoolean();
            if (mode == nameof(PlayMode.Offline)
                && line.TryGetProperty("PlayerCount", out var pc)
                && pc.GetInt32() == 1
                && !isSpecial)
            {
                Assert.Fail("Open-session v2 snapshot must not retain Offline/1 day lines after migration SQL.");
            }
        }
    }

    private static async Task<Guid> SeedOpenPreMigrationPcSessionAsync(
        AppDbContext db,
        string stationId,
        DateTime startUtc,
        DateTime checkpointUtc)
    {
        var snapshotJson = await BuildLegacySnapshotJsonAsync(db);
        var snap = new RateCardSnapshotEntity { Json = snapshotJson };
        db.RateCardSnapshots.Add(snap);
        await db.SaveChangesAsync();

        var sessionId = Guid.NewGuid();
        db.Stations.Add(new StationEntity
        {
            Id = stationId,
            GroupId = PilotSeed.PcNormalGroupId,
            DefaultPlayMode = PlayMode.Offline,
            OperationalState = StationOperationalState.Available,
        });
        db.Sessions.Add(new PlaySessionEntity
        {
            Id = sessionId,
            StationId = stationId,
            Status = SessionStatus.Open,
            StartedAtUtc = startUtc,
            Mode = PlayMode.Offline,
            PlayerCount = 1,
            RateCardSnapshotId = snap.Id,
            LastCheckpointUtc = checkpointUtc,
        });
        db.Segments.Add(new PlaySegmentEntity
        {
            SessionId = sessionId,
            SegmentKey = "a",
            StartUtc = startUtc,
            EndUtc = null,
            StationId = stationId,
            GroupId = PilotSeed.PcNormalGroupId,
            Mode = PlayMode.Offline,
            PlayerCount = 1,
            Kind = StationKind.Pc,
        });
        await db.SaveChangesAsync();
        return sessionId;
    }

    private static async Task<(Guid OpenSessionId, DateTime EndUtc, long ExpectedToman)> SeedPreMigrationPcSessionsAsync(
        AppDbContext db,
        string openStationId,
        string closedStationId,
        bool skipStations = false)
    {
        var snapshotJson = await BuildLegacySnapshotJsonAsync(db);
        var snapOpen = new RateCardSnapshotEntity { Json = snapshotJson };
        var snapClosed = new RateCardSnapshotEntity { Json = snapshotJson };
        db.RateCardSnapshots.AddRange(snapOpen, snapClosed);
        await db.SaveChangesAsync();

        var start = TestTimes.T(2026, 6, 3, 14, 0).UtcDateTime;
        var end = start.AddMinutes(45);
        const long expectedToman = 60_000;

        var openId = Guid.NewGuid();
        var closedId = Guid.NewGuid();
        if (!skipStations)
        {
            db.Stations.AddRange(
                new StationEntity
                {
                    Id = openStationId,
                    GroupId = PilotSeed.PcNormalGroupId,
                    DefaultPlayMode = PlayMode.Offline,
                    OperationalState = StationOperationalState.Available,
                },
                new StationEntity
                {
                    Id = closedStationId,
                    GroupId = PilotSeed.PcNormalGroupId,
                    DefaultPlayMode = PlayMode.Offline,
                    OperationalState = StationOperationalState.Available,
                });
        }

        db.Sessions.Add(new PlaySessionEntity
        {
            Id = openId,
            StationId = openStationId,
            Status = SessionStatus.Open,
            StartedAtUtc = start,
            Mode = PlayMode.Offline,
            PlayerCount = 1,
            RateCardSnapshotId = snapOpen.Id,
            LastCheckpointUtc = end,
        });
        db.Sessions.Add(new PlaySessionEntity
        {
            Id = closedId,
            StationId = closedStationId,
            Status = SessionStatus.Closed,
            StartedAtUtc = start,
            EndedAtUtc = end,
            Mode = PlayMode.Offline,
            PlayerCount = 1,
            RateCardSnapshotId = snapClosed.Id,
            LastCheckpointUtc = end,
        });
        db.Segments.Add(new PlaySegmentEntity
        {
            SessionId = openId,
            SegmentKey = "a",
            StartUtc = start,
            EndUtc = null,
            StationId = openStationId,
            GroupId = PilotSeed.PcNormalGroupId,
            Mode = PlayMode.Offline,
            PlayerCount = 1,
            Kind = StationKind.Pc,
        });
        db.Segments.Add(new PlaySegmentEntity
        {
            SessionId = closedId,
            SegmentKey = "a",
            StartUtc = start,
            EndUtc = end,
            StationId = closedStationId,
            GroupId = PilotSeed.PcNormalGroupId,
            Mode = PlayMode.Offline,
            PlayerCount = 1,
            Kind = StationKind.Pc,
        });
        await db.SaveChangesAsync();
        return (openId, end, expectedToman);
    }

    private static async Task<string> BuildLegacySnapshotJsonAsync(AppDbContext db)
    {
        var rounding = await db.BranchRounding.AsNoTracking().SingleAsync();
        var surcharge = await db.Database.SqlQueryRaw<long>(
            """SELECT "PcSpecialBandSurchargeToman" AS "Value" FROM branch_rounding LIMIT 1""").SingleAsync();
        var lines = await db.RateLines.AsNoTracking()
            .Select(l => new
            {
                l.GroupId,
                l.PlayMode,
                l.PlayerCount,
                l.IsSpecialBand,
                l.HourlyToman,
            })
            .ToListAsync();
        var payload = new
        {
            StepToman = rounding.StepToman,
            Direction = rounding.Direction,
            PcSpecialBandSurchargeToman = surcharge,
            Lines = lines,
        };
        return JsonSerializer.Serialize(payload);
    }

    private static async Task<long> ChargeTotalAsync(IServiceProvider services, Guid sessionId, DateTime endUtc)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var billing = scope.ServiceProvider.GetRequiredService<SessionBilling>();
        var session = await db.Sessions.AsNoTracking().Include(s => s.Segments).SingleAsync(s => s.Id == sessionId);
        var charge = await billing.BuildChargeAsync(session, endUtc, default);
        return charge.TotalAmountToman;
    }
}