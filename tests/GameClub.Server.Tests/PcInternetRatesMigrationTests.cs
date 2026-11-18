using System.Net.Http.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class PcInternetRatesMigrationTests(GameClubWebApplicationFactory factory)
{
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
}