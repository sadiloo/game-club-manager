using System.Net.Http.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class Ss14GameTariffMigrationTests(GameClubWebApplicationFactory factory)
{
    private const string PreSs14Migration = "20261116092721_StepPr37FixR1";

    [Fact]
    public async Task Ss14Migration_OpenSessionAmountsUnchanged_WithAndWithoutPrepaid()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-M1", 1);
        await S3TestHelpers.RegisterConsole(api, op, "PS5-M2", 1);
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var plainId = await S3TestHelpers.StartSession(api, "PS5-M1", 1);
        var prepaidId = await S3TestHelpers.StartSession(api, "PS5-M2", 1);
        await op.PostAsJsonAsync($"/api/operator/sessions/{prepaidId}/prepaid/amount", new { amountToman = 100_000L });
        factory.Clock.SetTehran(2026, 8, 8, 14, 25);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var prepaidBefore = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == prepaidId);
        var snapshotJson = await db.RateCardSnapshots.AsNoTracking()
            .Where(s => s.Id == prepaidBefore.RateCardSnapshotId)
            .Select(s => s.Json)
            .SingleAsync();
        await db.Database.MigrateAsync(PreSs14Migration);
        await db.Database.MigrateAsync();
        var plainAfter = await db.Sessions.AsNoTracking().Include(s => s.Segments).SingleAsync(s => s.Id == plainId);
        var prepaidAfter = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == prepaidId);
        Assert.All(plainAfter.Segments, s => Assert.Null(s.GameTitleId));
        Assert.Null(plainAfter.CurrentGameTitleId);
        Assert.Equal(100_000, prepaidAfter.PrepaidAmountBudgetToman);
        Assert.Equal(prepaidBefore.PrepaidBookedActiveSeconds, prepaidAfter.PrepaidBookedActiveSeconds);
        var snapshotAfter = await db.RateCardSnapshots.AsNoTracking()
            .Where(s => s.Id == prepaidAfter.RateCardSnapshotId)
            .Select(s => s.Json)
            .SingleAsync();
        Assert.Equal(snapshotJson, snapshotAfter);
    }
}