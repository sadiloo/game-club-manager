using System.Net;
using System.Net.Http.Json;
using GameClub.Server.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

public sealed class LocalServerStatusEndpointTests(GameClubWebApplicationFactory factory) : IClassFixture<GameClubWebApplicationFactory>
{
    [Fact]
    public async Task local_server_status_returns_404_off_loopback()
    {
        await using var isolated = new GameClubWebApplicationFactory
        {
            SimulatedRemoteIpAddress = IPAddress.Parse("203.0.113.7"),
        };
        var client = isolated.CreateClient();
        var response = await client.GetAsync("/api/local/server-status");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("startedAtUtc", body, StringComparison.Ordinal);
        Assert.DoesNotContain("dbOk", body, StringComparison.Ordinal);
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

    [Fact]
    public async Task local_server_status_db_ok_false_when_database_unreachable()
    {
        await using var isolated = new GameClubWebApplicationFactory
        {
            FailSelectOneProbe = true,
        };
        var client = isolated.CreateClient();
        SelectOneFaultInterceptor.FaultEnabled = true;
        try
        {
        var response = await client.GetAsync("/api/local/server-status");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LocalStatusDto>();
        Assert.NotNull(body);
        Assert.False(body!.DbOk);
        }
        finally
        {
            SelectOneFaultInterceptor.FaultEnabled = false;
        }
    }

    private sealed record LocalStatusDto(DateTimeOffset? StartedAtUtc, bool DbOk);
}