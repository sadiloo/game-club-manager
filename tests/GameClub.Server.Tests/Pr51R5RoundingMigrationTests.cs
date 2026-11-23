using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GameClub.Server.Tests;

public sealed class Pr51R5RoundingMigrationTests
{
    public const string MigrationId = "20261123190225_Pr51R5RoundingSettings";

    private const string TestDbPrefix = "gameclub_mig_r5_";

    private static string ResolveConnectionString() =>
        Environment.GetEnvironmentVariable("GAMECLUB_TEST_DATABASE")
        ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE")
        ?? "Host=127.0.0.1;Port=5432;Database=gameclub_test;Username=gameclub;Password=gameclub";

    private static async Task<(string ConnectionString, string DatabaseName)> FreshDbAsync()
    {
        var template = new NpgsqlConnectionStringBuilder(ResolveConnectionString());
        var dbName = TestDbPrefix + Guid.NewGuid().ToString("N");
        var admin = new NpgsqlConnectionStringBuilder(template.ConnectionString) { Database = "postgres" };
        await using var adminConn = new NpgsqlConnection(admin.ConnectionString);
        await adminConn.OpenAsync();
        await using (var cmd = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", adminConn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        template.Database = dbName;
        return (template.ConnectionString, dbName);
    }

    private static async Task DropDbAsync(string dbName)
    {
        var admin = new NpgsqlConnectionStringBuilder(ResolveConnectionString()) { Database = "postgres" };
        await using var adminConn = new NpgsqlConnection(admin.ConnectionString);
        await adminConn.OpenAsync();
        await using (var cmd = new NpgsqlCommand(
            """SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid()""",
            adminConn))
        {
            cmd.Parameters.AddWithValue("db", dbName);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\"", adminConn))
        {
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task migration_listed_and_applies_on_fresh_database()
    {
        var (cs, dbName) = await FreshDbAsync();
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options;
            await using var db = new AppDbContext(options);
            await db.Database.MigrateAsync();
            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            Assert.Contains(MigrationId, applied);
            var row = await db.BranchRounding.AsNoTracking().SingleAsync();
            Assert.True(row.RoundingEnabled);
            Assert.Equal(5_000, row.StepToman);
            Assert.Equal("Up", row.Direction);
            Assert.True(row.ApplyToWallet);
            Assert.Equal(5_000, row.WalletMinBillToman);
            Assert.True(row.AllowWalletShortBalanceDebt);
        }
        finally
        {
            await DropDbAsync(dbName);
        }
    }

    [Fact]
    public async Task migration_applies_defaults_on_existing_database_without_new_columns()
    {
        var (cs, dbName) = await FreshDbAsync();
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options;
            await using var db = new AppDbContext(options);
            await db.Database.MigrateAsync("20261117041656_Ss14FixR1");
            if (!await db.BranchRounding.AnyAsync())
            {
                db.BranchRounding.Add(new BranchRoundingEntity
                {
                    StepToman = 5_000,
                    Direction = "Up",
                    PcSpecialBandSurchargeToman = 20_000,
                });
                await db.SaveChangesAsync();
            }

            await db.Database.MigrateAsync();
            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            Assert.Contains(MigrationId, applied);
            var row = await db.BranchRounding.AsNoTracking().SingleAsync();
            Assert.True(row.RoundingEnabled);
            Assert.True(row.ApplyToWallet);
            Assert.Equal(5_000, row.WalletMinBillToman);
            Assert.True(row.AllowWalletShortBalanceDebt);
        }
        finally
        {
            await DropDbAsync(dbName);
        }
    }
}