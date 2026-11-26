using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GameClub.Server.Tests;

/// <summary>
/// InitialUsers seeding rules (Q2, PO-D, DEF-13b). Each test seeds into its own uniquely named, freshly migrated
/// database (gameclub_test_seedval_*) so the shared "server" collection database is never truncated.
/// </summary>
public sealed class DatabaseSeedInitialUsersValidationTests
{
    private const string Hash = "deadbeef";

    private static IConfiguration Config(params (string? Username, string? Role, string? PinHash)[] users)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < users.Length; i++)
        {
            values[$"InitialUsers:{i}:Username"] = users[i].Username;
            values[$"InitialUsers:{i}:Role"] = users[i].Role;
            values[$"InitialUsers:{i}:PinHash"] = users[i].PinHash;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task WithIsolatedDbAsync(Func<AppDbContext, Task> body)
    {
        using var isolated = new GameClubPrefixedTestDatabase(GameClubPrefixedTestDatabase.NamePrefix + "seedval_");
        isolated.EnsureInitialized();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(isolated.ConnectionString).Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();
        Assert.False(await db.OperatorUsers.AnyAsync());
        await body(db);
    }

    private static Task AssertSeedingRefusedAsync(IConfiguration? config, string expectedMessage, bool allowDefaults = false) =>
        WithIsolatedDbAsync(async db =>
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => DatabaseSeed.EnsureAsync(db, config, allowDefaultDevelopmentUsers: allowDefaults));
            Assert.Contains(expectedMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
            db.ChangeTracker.Clear();
            Assert.False(await db.OperatorUsers.AnyAsync());
        });

    [Fact]
    public Task initial_users_blank_username_is_refused() =>
        AssertSeedingRefusedAsync(Config(("  ", "Manager", Hash)), "blank username");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public Task initial_users_blank_pin_hash_is_refused(string? pinHash) =>
        AssertSeedingRefusedAsync(Config(("boss", "Manager", pinHash)), "blank pin hash");

    [Fact]
    public Task initial_users_unknown_role_is_refused() =>
        AssertSeedingRefusedAsync(Config(("badrole", "NotARealRole", Hash)), "unknown role 'NotARealRole'");

    // R10f item 9 (rr4): a blank or missing role is refused, never defaulted to some role.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public Task initial_users_blank_or_null_role_is_refused(string? role) =>
        AssertSeedingRefusedAsync(Config(("boss", "Manager", Hash), ("norole", role, Hash)), $"unknown role '{role}'");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void try_parse_role_name_rejects_blank_or_null(string? role)
    {
        Assert.False(DatabaseSeed.TryParseRoleName(role, out var parsed));
        Assert.Equal(default, parsed);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData(" 1 ")]
    [InlineData("+1")]
    [InlineData("Manager, Operator")]
    public Task initial_users_numeric_or_combined_role_is_refused(string role) =>
        AssertSeedingRefusedAsync(Config(("boss", "Manager", Hash), ("numrole", role, Hash)), $"unknown role '{role}'");

    [Fact]
    public Task initial_users_without_a_manager_is_refused() =>
        AssertSeedingRefusedAsync(Config(("op1", "Operator", Hash), ("owner", "Owner", Hash)), "at least one Manager is required");

    [Fact]
    public Task empty_initial_users_is_refused_and_never_creates_default_pins() =>
        AssertSeedingRefusedAsync(new ConfigurationBuilder().Build(), "no InitialUsers configured");

    [Fact]
    public Task null_configuration_without_development_defaults_is_refused() =>
        AssertSeedingRefusedAsync(null, "no InitialUsers configured");

    [Fact]
    public Task role_names_are_case_insensitive_and_seeded_as_given() =>
        WithIsolatedDbAsync(async db =>
        {
            await DatabaseSeed.EnsureAsync(db, Config(("boss", "manager", Hash), ("desk", " OPERATOR ", Hash)));
            var users = await db.OperatorUsers.OrderBy(u => u.Username).ToListAsync();
            Assert.Equal(2, users.Count);
            Assert.Equal(("boss", UserRole.Manager, Hash), (users[0].Username, users[0].Role, users[0].PinHash));
            Assert.Equal(("desk", UserRole.Operator, Hash), (users[1].Username, users[1].Role, users[1].PinHash));
        });

    /// <summary>DEF-13b end to end: the exact server.json shape installer/tools/server-postinstall.ps1 writes
    /// (accounts phase; pinned by scripts/installer-wine/Invoke-BoxPostinstallUpgradeRestore.ps1) seeds role names.</summary>
    [Fact]
    public Task installer_server_json_shape_seeds_two_managers_and_an_operator() =>
        WithIsolatedDbAsync(async db =>
        {
            const string serverJson = """
                {
                  "ConnectionStrings": { "Default": "Host=127.0.0.1;Port=5433;Database=checkpoint;Username=postgres;Password=x" },
                  "OperatorSessionSecret": "s",
                  "GAMECLUB_API_KEY": "k",
                  "Urls": "http://0.0.0.0:5000",
                  "InitialUsers": [
                    { "username": "m1", "role": "Manager", "pinHash": "hash:5678" },
                    { "username": "m2", "role": "Manager", "pinHash": "hash:8765" },
                    { "username": "op", "role": "Operator", "pinHash": "hash:4321" }
                  ]
                }
                """;
            var config = new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(serverJson)))
                .Build();
            await DatabaseSeed.EnsureAsync(db, config);
            var users = await db.OperatorUsers.OrderBy(u => u.Username).Select(u => new { u.Username, u.Role, u.PinHash }).ToListAsync();
            Assert.Equal(
                new[] { ("m1", UserRole.Manager, "hash:5678"), ("m2", UserRole.Manager, "hash:8765"), ("op", UserRole.Operator, "hash:4321") },
                users.Select(u => (u.Username, u.Role, u.PinHash)).ToArray());
        });

    [Fact]
    public Task development_defaults_only_when_explicitly_allowed() =>
        WithIsolatedDbAsync(async db =>
        {
            await DatabaseSeed.EnsureAsync(db, new ConfigurationBuilder().Build(), allowDefaultDevelopmentUsers: true);
            var users = await db.OperatorUsers.OrderBy(u => u.Username).ToListAsync();
            Assert.Equal(new[] { "manager", "operator" }, users.Select(u => u.Username).ToArray());
        });

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(false, "", false)]
    [InlineData(false, "Host=localhost;Database=checkpoint_copy;Username=x;Password=y", false)]
    [InlineData(false, "Host=localhost;Database=gameclub;Username=x;Password=y", false)]
    // R10f item 5: only the gameclub_test_ prefix (with underscore) counts; plain gameclub_test or lookalikes do not.
    [InlineData(false, "Host=localhost;Database=gameclub_test;Username=x;Password=y", false)]
    [InlineData(false, "Host=localhost;Database=gameclub_testx;Username=x;Password=y", false)]
    [InlineData(false, "Host=localhost;Database=gameclub_test_ci;Username=x;Password=y", true)]
    [InlineData(false, "Host=localhost;Database=gameclub_test_abc;Username=x;Password=y", true)]
    public void default_pins_policy_is_development_or_isolated_gameclub_test_only(bool isDevelopment, string? isolatedCs, bool expected) =>
        Assert.Equal(expected, ServerApplication.AllowDefaultDevelopmentUsers(isDevelopment, isolatedCs));
}
