using System.Net;
using System.Net.Http.Json;
using GameClub.Server.Controllers;
using GameClub.Server.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

public sealed class LocalServerStatusEndpointTests(GameClubWebApplicationFactory factory) : IClassFixture<GameClubWebApplicationFactory>
{
    [Fact]
    public async Task local_server_status_returns_404_off_loopback()
    {
        Assert.False(LocalOpenSessionsController.IsLoopbackAddress(System.Net.IPAddress.Parse("203.0.113.7")));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-key");
        var ok = await client.GetAsync("/api/local/server-status");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task local_server_status_loopback_200_with_started_at_and_db_ok()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var runtime = scope.ServiceProvider.GetRequiredService<ServerRuntimeStatus>();
            runtime.ApplicationStartedAtUtc = DateTimeOffset.Parse("2026-03-01T08:00:00Z");
        }

        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/local/server-status");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LocalStatusDto>();
        Assert.NotNull(body);
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T08:00:00Z"), body!.StartedAtUtc);
        Assert.True(body.DbOk);
    }

    private sealed record LocalStatusDto(DateTimeOffset? StartedAtUtc, bool DbOk);
}