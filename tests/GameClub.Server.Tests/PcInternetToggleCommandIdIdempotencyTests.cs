using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class PcInternetToggleCommandIdIdempotencyTests(GameClubWebApplicationFactory factory)
{
    private static async Task EnsureOfflineRatesAsync(HttpClient mgr)
    {
        (await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new { onlineDay = 80_000L, onlineNight = 100_000L, offlineDay = 60_000L, offlineNight = 80_000L }))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task PcInternetToggle_DuplicateCommandId_Idempotent_NoExtraState()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        const string stationId = "PC-CMD-ID";
        await S3TestHelpers.RegisterPc(api, stationId, PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, stationId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var expectedVersion = await db.Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
        var cmd = Guid.NewGuid();
        var request = new { on = false, expectedVersion, commandId = cmd };

        var first = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", request);
        first.EnsureSuccessStatusCode();
        var firstJson = await first.Content.ReadAsStringAsync();

        var segmentsAfterFirst = await db.Segments.CountAsync(s => s.SessionId == sessionId);
        var sessionAfterFirst = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        var desiredVersionAfterFirst = await db.StationInternetStates.AsNoTracking()
            .Where(s => s.StationId == stationId).Select(s => s.DesiredVersion).SingleAsync();
        var internetAuditsAfterFirst = await db.AuditLog.CountAsync(a => a.Action == "session.internet");
        var commandRowsAfterFirst = await db.SessionCommands.CountAsync(c => c.SessionId == sessionId && c.CommandId == cmd);

        Assert.Equal(2, segmentsAfterFirst);
        Assert.Equal(PlayMode.Offline, sessionAfterFirst.Mode);
        Assert.Equal(1, internetAuditsAfterFirst);
        Assert.Equal(1, commandRowsAfterFirst);

        var second = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", request);
        second.EnsureSuccessStatusCode();
        var secondJson = await second.Content.ReadAsStringAsync();

        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(firstJson, secondJson);

        Assert.Equal(segmentsAfterFirst, await db.Segments.CountAsync(s => s.SessionId == sessionId));
        var sessionAfterSecond = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.Equal(sessionAfterFirst.RowVersion, sessionAfterSecond.RowVersion);
        Assert.Equal(sessionAfterFirst.Mode, sessionAfterSecond.Mode);
        Assert.Equal(
            desiredVersionAfterFirst,
            await db.StationInternetStates.AsNoTracking()
                .Where(s => s.StationId == stationId).Select(s => s.DesiredVersion).SingleAsync());
        Assert.Equal(internetAuditsAfterFirst, await db.AuditLog.CountAsync(a => a.Action == "session.internet"));
        Assert.Equal(commandRowsAfterFirst, await db.SessionCommands.CountAsync(c => c.SessionId == sessionId && c.CommandId == cmd));
    }
}