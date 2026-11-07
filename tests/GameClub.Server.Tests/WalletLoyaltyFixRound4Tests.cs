using System.Net;
using System.Net.Http.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class WalletLoyaltyFixRound4Tests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _api = factory.CreateClientWithKey();

    private async Task<HttpClient> MgrAsync() => await factory.CreateOperatorClientAsync("manager", "1234");

    private async Task<HttpClient> OpAsync() => await factory.CreateOperatorClientAsync();

    private static async Task<Guid> CreateCustomerAsync(HttpClient op, string mobile)
    {
        var response = await op.PostAsJsonAsync("/api/operator/customers", new { name = "عضو", mobile });
        return (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage[]> RaceAsync(params Func<Task<HttpResponseMessage>>[] actions)
    {
        using var gate = new ManualResetEventSlim(false);
        var tasks = actions.Select(a => Task.Run(async () =>
        {
            gate.Wait();
            return await a();
        })).ToArray();
        gate.Set();
        return await Task.WhenAll(tasks);
    }

    private static async Task<(long Real, long Gift, long Spendable, long Ledger)> WalletStateAsync(
        GameClubWebApplicationFactory factory,
        Guid customerId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var member = await db.CustomerMembers.AsNoTracking().FirstAsync(m => m.CustomerId == customerId);
        var ledger = await db.WalletLedger.AsNoTracking().Where(e => e.CustomerId == customerId)
            .SumAsync(e => e.RealDeltaToman + e.GiftDeltaToman);
        return (member.RealToman, member.GiftToman, member.RealToman + member.GiftToman, ledger);
    }

    [Fact]
    public async Task round4_package_void_concurrent_cash_topup_leaves_spendable_matching_ledger()
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await S3TestHelpers.ResetAsync(factory);
            var mgr = await MgrAsync();
            var op = await OpAsync();
            var op2 = await OpAsync();
            var customerId = await CreateCustomerAsync(op, $"0912414{attempt:D4}");
            await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
                new { method = "Cash", amountToman = 300_000L });
            await mgr.PutAsJsonAsync("/api/operator/members/packages/pkg-r4-race",
                new { name = "بسته", priceToman = 220_000L, minutes = 30L });
            await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/packages/sell",
                new { productId = "pkg-r4-race", payments = new[] { new { method = "Wallet", amountToman = 220_000L } } });
            long invoiceNumber;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                invoiceNumber = await db.Invoices.Where(i => i.CustomerId == customerId).Select(i => i.Number).SingleAsync();
            }

            var responses = await RaceAsync(
                () => mgr.PostAsJsonAsync($"/api/operator/invoices/{invoiceNumber}/void", new { reason = "اشتباه" }),
                () => op2.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
                    new { method = "Cash", amountToman = 10_000L }));
            Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK));

            var (_, _, spendable, ledger) = await WalletStateAsync(factory, customerId);
            Assert.Equal(310_000, spendable);
            Assert.Equal(ledger, spendable);
            using var verifyScope = factory.Services.CreateScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Empty(await verifyDb.CustomerPackages.Where(p => p.CustomerId == customerId).ToListAsync());
            Assert.Contains(await verifyDb.WalletLedger.Where(e => e.CustomerId == customerId).ToListAsync(),
                e => e.Kind == WalletLedgerKind.InvoiceVoidReversal && e.Label == "package_sale_void");
            Assert.Contains(await verifyDb.WalletLedger.Where(e => e.CustomerId == customerId).ToListAsync(),
                e => e.Kind == WalletLedgerKind.TopUpReal && e.RealDeltaToman == 10_000);
        }
    }

    [Fact]
    public async Task round4_live_session_bill_exposes_level_and_birthday_discount_lines()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await MgrAsync();
        var op = await OpAsync();
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var customerId = await CreateCustomerAsync(mgr, "09124140099");
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/opening-balance",
            new { lifetimeHours = 50L });
        await mgr.PutAsJsonAsync($"/api/operator/members/customers/{customerId}/birthday",
            new { year = 1380, month = 3, day = 13 });
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-R4DISC", 1);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-R4DISC", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 1);
        (await _api.PostAsync($"/api/sessions/{sessionId}/stop", null)).EnsureSuccessStatusCode();
        var bill = await op.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal(91, bill.GetProperty("levelDiscountToman").GetInt64());
        Assert.Equal(183, bill.GetProperty("birthdayDiscountToman").GetInt64());
        Assert.Equal(1_559, bill.GetProperty("unroundedPayableToman").GetInt64());
        Assert.Equal(1_833, bill.GetProperty("timeGrossToman").GetInt64());
    }

    [Fact]
    public async Task round4_live_session_bill_qa_fixture_discount_oracle()
    {
        await S3TestHelpers.ResetAsync(factory);
        var groupId = GameTimerReference.Ps5GroupId;
        static string RateSql(string gid, long rate) =>
            $"""UPDATE rate_lines SET "HourlyToman" = {rate} WHERE "GroupId" = '{gid}' AND "PlayerCount" = 1 AND "PlayMode" = 'Offline' AND "IsSpecialBand" = false""";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(RateSql(groupId, 3_000_000));
        try
        {
            var mgr = await MgrAsync();
            var op = await OpAsync();
            factory.Clock.SetTehran(2026, 6, 3, 14, 0);
            var customerId = await CreateCustomerAsync(mgr, "09124140100");
            await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/opening-balance",
                new { lifetimeHours = 50L });
            await mgr.PutAsJsonAsync($"/api/operator/members/customers/{customerId}/birthday",
                new { year = 1380, month = 3, day = 13 });
            await S3TestHelpers.RegisterConsole(_api, op, "PS5-R4QA", 1);
            var sessionId = await S3TestHelpers.StartSession(_api, "PS5-R4QA", 1);
            await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
            await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines",
                new { catalogItemId = CatalogSeed.ChipsId });
            factory.Clock.SetTehran(2026, 6, 3, 14, 2);
            (await _api.PostAsync($"/api/sessions/{sessionId}/stop", null)).EnsureSuccessStatusCode();
            var bill = await op.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/operator/sessions/{sessionId}/bill");
            Assert.Equal(100_000, bill.GetProperty("timeGrossToman").GetInt64());
            Assert.Equal(20_000, bill.GetProperty("buffetGrossToman").GetInt64());
            Assert.Equal(5_000, bill.GetProperty("levelDiscountToman").GetInt64());
            Assert.Equal(10_000, bill.GetProperty("birthdayDiscountToman").GetInt64());
            Assert.Equal(105_000, bill.GetProperty("collectibleToman").GetInt64());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(RateSql(groupId, 110_000));
        }
    }
}
