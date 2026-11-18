#if NET10_0_OR_GREATER
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

/// <summary>Ensure live Postgres schema exists before truncate-only reset (order-independent).</summary>
internal static class LiveDatabaseMigration
{
    public static async Task EnsureMigratedAsync(string? connectionString = null)
    {
        connectionString ??= Environment.GetEnvironmentVariable("GAMECLUB_TEST_DATABASE")
            ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE")
            ?? throw new InvalidOperationException("GAMECLUB_DATABASE is not set.");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();
    }
}
#endif