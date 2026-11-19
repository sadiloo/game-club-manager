using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

public sealed class OperatorPanelServerStatusChipTests(GameClubWebApplicationFactory factory) : IClassFixture<GameClubWebApplicationFactory>
{
    [Fact]
    public async Task operator_panel_server_status_chip_polls_and_shows_persian_lines()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var runtime = scope.ServiceProvider.GetRequiredService<Hosting.ServerRuntimeStatus>();
            runtime.ApplicationStartedAtUtc = DateTimeOffset.Parse("2026-06-01T06:30:00Z");
        }

        var op = await factory.CreateOperatorClientAsync();
        var status = await op.GetFromJsonAsync<StatusDto>("/api/operator/server-status");
        Assert.NotNull(status);
        Assert.NotNull(status!.StartedAtUtc);
        var html = await factory.CreateClient().GetStringAsync("/operator/index.html");
        Assert.Contains("serverStatusChip", html);
    }

    private sealed record StatusDto(DateTimeOffset? StartedAtUtc, string Version, DateTimeOffset? LastBackupUtc);
}