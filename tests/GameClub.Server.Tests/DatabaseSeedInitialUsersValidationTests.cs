using GameClub.Server.Data;
using GameClub.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GameClub.Server.Tests;

/// <summary>
/// InitialUsers seeding refusal (Q2). Each test seeds into its own uniquely named, freshly migrated
/// database (gameclub_test_seedval_*) so the shared "server" collection database is never truncated.
/// </summary>
public sealed class DatabaseSeedInitialUsersValidationTests
{
    private static async Task AssertSeedingRefusedAsync(string username, string? role, string pinHash, string expectedMessage)
    {
        using var isolated = new GameClubPrefixedTestDatabase(GameClubPrefixedTestDatabase.NamePrefix + "seedval_");
        isolated.EnsureInitialized();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(isolated.ConnectionString).Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();
        Assert.False(await db.OperatorUsers.AnyAsync());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InitialUsers:0:Username"] = username,
                ["InitialUsers:0:Role"] = role,
                ["InitialUsers:0:PinHash"] = pinHash,
            })
            .Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseSeed.EnsureAsync(db, config));
        Assert.Contains(expectedMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
        db.ChangeTracker.Clear();
        Assert.False(await db.OperatorUsers.AnyAsync());
    }

    [Fact]
    public Task initial_users_blank_username_is_refused() =>
        AssertSeedingRefusedAsync("  ", "Manager", "deadbeef", "blank username");

    [Fact]
    public Task initial_users_unknown_role_is_refused() =>
        AssertSeedingRefusedAsync("badrole", "NotARealRole", "deadbeef", "unknown role");

    [Fact]
    public Task initial_users_numeric_undefined_role_is_refused() =>
        AssertSeedingRefusedAsync("numrole", "99", "deadbeef", "unknown role '99'");
}
