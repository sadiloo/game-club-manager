using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using GameClub.Server.Hosting;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class OperatorServerStatusEndpointTests(GameClubWebApplicationFactory factory) : IClassFixture<GameClubWebApplicationFactory>
{
    [Fact]
    public async Task server_status_returns_401_without_operator_cookie()
    {
        var response = await factory.CreateClient().GetAsync("/api/operator/server-status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task server_status_returns_started_at_utc_and_last_backup_from_newest_file()
    {
        var backupDir = Path.Combine(Path.GetTempPath(), $"cp-backups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupDir);
        var older = Path.Combine(backupDir, "a.sql");
        var newer = Path.Combine(backupDir, "b.sql");
        await File.WriteAllTextAsync(older, "old");
        await Task.Delay(50);
        await File.WriteAllTextAsync(newer, "new");
        var newestWrite = File.GetLastWriteTimeUtc(newer);

        Environment.SetEnvironmentVariable("CHECKPOINT_BACKUPS_PATH", backupDir);
        try
        {
        using (var scope = factory.Services.CreateScope())
        {
            var runtime = scope.ServiceProvider.GetRequiredService<ServerRuntimeStatus>();
            runtime.ApplicationStartedAtUtc = DateTimeOffset.Parse("2026-01-02T10:00:00Z");
        }

        var op = await factory.CreateOperatorClientAsync();
        var response = await op.GetAsync("/api/operator/server-status");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonServerStatus>();
        Assert.NotNull(body);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T10:00:00Z"), body!.StartedAtUtc);
        Assert.NotNull(body.LastBackupUtc);
        Assert.Equal(newestWrite, body.LastBackupUtc!.Value.UtcDateTime, precision: TimeSpan.FromSeconds(2));
        Assert.True(body.DbOk);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHECKPOINT_BACKUPS_PATH", null);
            Directory.Delete(backupDir, true);
        }
    }

    private sealed record JsonServerStatus(DateTimeOffset? StartedAtUtc, string Version, DateTimeOffset? LastBackupUtc, bool DbOk);
}