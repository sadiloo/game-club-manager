using GameClub.Server.Data;
using GameClub.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class DatabaseSeedInitialUsersValidationTests(GameClubWebApplicationFactory factory)
{
    private async Task ClearOperatorUsersAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE operator_users RESTART IDENTITY CASCADE;");
    }

    [Fact]
    public async Task initial_users_blank_username_is_refused()
    {
        await ClearOperatorUsersAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InitialUsers:0:Username"] = "  ",
                ["InitialUsers:0:Role"] = "Manager",
                ["InitialUsers:0:PinHash"] = "deadbeef",
            })
            .Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseSeed.EnsureAsync(db, config));
        Assert.Contains("blank username", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await db.OperatorUsers.AnyAsync());
    }

    [Fact]
    public async Task initial_users_unknown_role_is_refused()
    {
        await ClearOperatorUsersAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InitialUsers:0:Username"] = "badrole",
                ["InitialUsers:0:Role"] = "NotARealRole",
                ["InitialUsers:0:PinHash"] = "deadbeef",
            })
            .Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseSeed.EnsureAsync(db, config));
        Assert.Contains("unknown role", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await db.OperatorUsers.AnyAsync());
    }
}