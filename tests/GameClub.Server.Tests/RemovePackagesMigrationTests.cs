using System.Net.Http.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GameClub.Server.Tests;

public sealed class RemovePackagesMigrationTests
{
    const string TestDbPrefix = "gameclub_mig_test_";

    static string ResolveConnectionString() =>
        Environment.GetEnvironmentVariable("GAMECLUB_TEST_DATABASE")
        ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE")
        ?? "Host=localhost;Database=gameclub_test;Username=gameclub;Password=gameclub";

    static async Task<string> FreshDbAsync()
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
        return template.ConnectionString;
    }

    [Fact]
    public async Task migration_guard_stops_on_customer_packages_row()
    {
        var cs = await FreshDbAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync("20261108171252_20261108_StepPcKioskScope");
        var customerId = Guid.NewGuid();
        db.Customers.Add(new CustomerEntity { Id = customerId, Name = "x", Mobile = "09120000000", CreatedAtUtc = DateTime.UtcNow });
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customerId });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(
            """INSERT INTO customer_packages ("Id","CustomerId","Name","RemainingMinutes","FaceValueToman") VALUES ({0},{1},'p',60,1000)""",
            Guid.NewGuid(), customerId);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
    }

    [Fact]
    public async Task migration_guard_stops_on_wallet_ledger_package_purchase_kind()
    {
        var cs = await FreshDbAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync("20261108171252_20261108_StepPcKioskScope");
        var customerId = Guid.NewGuid();
        db.Customers.Add(new CustomerEntity { Id = customerId, Name = "x", Mobile = "09120000001", CreatedAtUtc = DateTime.UtcNow });
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customerId });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO wallet_ledger ("CustomerId","AtUtc","Kind","RealDeltaToman","GiftDeltaToman")
            VALUES ({0}, NOW() AT TIME ZONE 'utc', 'PackagePurchase', 0, 0)
            """,
            customerId);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
        Assert.Contains("PackagePurchase", ex.MessageText);
    }

}

[Collection("server")]
public sealed class RemovePackagesMigrationLiveTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task migration_preserves_open_session_wallet_and_pending_topup()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await S3TestHelpers.RegisterPc(api, "PC-MIG", GameClub.Billing.PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-MIG", 1);
        var customerId = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "m", mobile = "09129990001" })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode()));
        var customerJson = await customerId.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var cid = customerJson.GetProperty("id").GetGuid();
        await op.PostAsJsonAsync($"/api/operator/members/customers/{cid}/wallet/top-up",
            new { method = "CardToCard", amountToman = 10_000L, destinationCardId = "card-a", reference = "mig", commandId = Guid.NewGuid()});
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var beforeSessions = await db.Sessions.CountAsync();
        var beforePending = await db.PendingWalletTopUps.CountAsync();
        var beforeWallet = await db.CustomerMembers.AsNoTracking().Where(m => m.CustomerId == cid).Select(m => m.RealToman).FirstAsync();
        await db.Database.MigrateAsync();
        Assert.Equal(beforeSessions, await db.Sessions.CountAsync());
        Assert.Equal(beforePending, await db.PendingWalletTopUps.CountAsync());
        Assert.Equal(beforeWallet, await db.CustomerMembers.AsNoTracking().Where(m => m.CustomerId == cid).Select(m => m.RealToman).FirstAsync());
        Assert.Equal(0, (await db.PendingWalletTopUps.AsNoTracking().FirstAsync()).GiftToman);
        _ = sessionId;
    }
}