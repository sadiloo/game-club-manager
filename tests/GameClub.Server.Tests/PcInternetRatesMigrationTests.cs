using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using Microsoft.EntityFrameworkCore;
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

    private static async Task WithPrePcInternetSeedAsync(GameClubWebApplicationFactory factory, Func<AppDbContext, Task> body)
    {
        await S3TestHelpers.ResetAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            await db.Database.MigrateAsync(PrePcInternetMigration);
            await DatabaseSeed.EnsureAsync(db);
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
}