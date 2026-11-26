using System.Net;
using System.Net.Http.Json;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class LocalOpenSessionsEndpointTests(GameClubWebApplicationFactory factory) : IClassFixture<GameClubWebApplicationFactory>
{
    [Fact]
    public async Task open_sessions_returns_404_without_api_key_or_non_loopback()
    {
        var noKey = factory.CreateClient();
        var noKeyResponse = await noKey.GetAsync("/api/local/open-sessions");
        Assert.Equal(HttpStatusCode.NotFound, noKeyResponse.StatusCode);
        Assert.False(Controllers.LocalOpenSessionsController.IsLoopbackAddress(System.Net.IPAddress.Parse("203.0.113.7")));
    }

    [Fact]
    public async Task open_sessions_count_includes_open_and_paused_only()
    {
        await S3TestHelpers.ResetAsync(factory);
        var client = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterPc(client, "pc-a", "pc-normal");
        await S3TestHelpers.RegisterPc(client, "pc-b", "pc-normal");

        var s1 = await S3TestHelpers.StartSession(client, "pc-a");
        var s2 = await S3TestHelpers.StartSession(client, "pc-b");
        await op.PostAsJsonAsync($"/api/operator/sessions/{s2}/pause", new { });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
            var closed = await db.Sessions.FirstAsync(s => s.Id == s1);
            closed.Status = SessionStatus.Closed;
            await db.SaveChangesAsync();
        }

        var response = await factory.CreateClientWithKey().GetAsync("/api/local/open-sessions");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonOpenSessions>();
        Assert.NotNull(body);
        Assert.Equal(1, body!.Count);
    }

    private sealed record JsonOpenSessions(int Count);
}