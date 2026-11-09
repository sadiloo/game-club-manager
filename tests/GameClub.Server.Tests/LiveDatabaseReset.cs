using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Tests;

/// <summary>Reset a live out-of-process server database (windows-2022 kiosk integration).</summary>
public static class LiveDatabaseReset
{
    public static async Task ResetAsync(string? connectionString = null)
    {
        connectionString ??= Environment.GetEnvironmentVariable("GAMECLUB_TEST_DATABASE")
            ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE")
            ?? throw new InvalidOperationException("GAMECLUB_DATABASE is not set.");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new AppDbContext(options);
        await db.Database.ExecuteSqlRawAsync(S3TestHelpers.TruncateSql);
        await DatabaseSeed.EnsureAsync(db);
        if (!await db.GameCatalogCategories.AnyAsync(c => c.Id == 2))
        {
            db.GameCatalogCategories.Add(new GameCatalogCategoryEntity { Id = 2, Name = "اختصاصی", SortOrder = 2 });
            await db.SaveChangesAsync();
        }
    }
}
