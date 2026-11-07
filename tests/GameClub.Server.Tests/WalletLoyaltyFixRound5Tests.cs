using System.Net;
using System.Net.Http.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class WalletLoyaltyFixRound5Tests(GameClubWebApplicationFactory factory)
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

    private async Task<long> SeedWalletPlayInvoiceAsync(HttpClient op, Guid customerId, int attempt)
    {
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 300_000L });
        var station = $"PS5-R5-{attempt:D4}";
        await S3TestHelpers.RegisterConsole(_api, op, station, 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, station, 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        var bill = await op.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        var collect = bill.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(55_000, collect);
        var confirm = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = collect } } });
        confirm.EnsureSuccessStatusCode();
        var body = await confirm.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("number").GetInt64();
    }

    [Fact]
    public async Task round5_play_invoice_void_concurrent_cash_topup_leaves_spendable_matching_ledger()
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await S3TestHelpers.ResetAsync(factory);
            var mgr = await MgrAsync();
            var op = await OpAsync();
            var op2 = await OpAsync();
            var customerId = await CreateCustomerAsync(op, $"0912515{attempt:D4}");
            var invoiceNumber = await SeedWalletPlayInvoiceAsync(op, customerId, attempt);

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
            Assert.Contains(await verifyDb.WalletLedger.Where(e => e.CustomerId == customerId).ToListAsync(),
                e => e.Kind == WalletLedgerKind.InvoiceVoidReversal && e.Label == "void_reversal");
            Assert.Contains(await verifyDb.WalletLedger.Where(e => e.CustomerId == customerId).ToListAsync(),
                e => e.Kind == WalletLedgerKind.TopUpReal && e.RealDeltaToman == 10_000);
        }
    }

    [Fact]
    public async Task round5_play_invoice_void_concurrent_buffet_wallet_spend_leaves_spendable_matching_ledger()
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await S3TestHelpers.ResetAsync(factory);
            var mgr = await MgrAsync();
            var op = await OpAsync();
            var op2 = await OpAsync();
            var customerId = await CreateCustomerAsync(op, $"0912525{attempt:D4}");
            var invoiceNumber = await SeedWalletPlayInvoiceAsync(op, customerId, attempt);

            var bo = (await op2.PostAsync("/api/operator/buffet-only/start", null)).EnsureSuccessStatusCode();
            var openSaleId = (await bo.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("openSaleId").GetGuid();
            await op2.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
            await op2.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/lines",
                new { catalogItemId = CatalogSeed.ChipsId });
            await op2.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null);

            var responses = await RaceAsync(
                () => mgr.PostAsJsonAsync($"/api/operator/invoices/{invoiceNumber}/void", new { reason = "اشتباه" }),
                () => op2.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm",
                    new { payments = new[] { new { method = "Wallet", amountToman = 20_000L } } }));
            Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK));

            var (_, _, spendable, ledger) = await WalletStateAsync(factory, customerId);
            Assert.Equal(280_000, spendable);
            Assert.Equal(ledger, spendable);
        }
    }
}
