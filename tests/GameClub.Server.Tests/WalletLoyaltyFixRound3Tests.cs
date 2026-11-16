using System.Net;
using System.Net.Http.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class WalletLoyaltyFixRound3Tests(GameClubWebApplicationFactory factory)
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
    public async Task round3_a1_ten_one_toman_wallet_spends_split_cumulatively()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await OpAsync();
        var mgr = await MgrAsync();
        await mgr.PutAsJsonAsync($"/api/operator/catalog/items/penny", new
        {
            categoryId = CatalogSeed.FoodCategoryId,
            name = "یک تومان",
            kind = "Unstocked",
            unitPriceToman = 1L,
            stockQuantity = (int?)null,
            lowStockWarningThreshold = (int?)null,
        });
        var customerId = await CreateCustomerAsync(op, "09123130001");
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 80_000L, commandId = Guid.NewGuid()});
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/gift",
            new { giftToman = 20_000L });
        for (var i = 0; i < 10; i++)
        {
            var bo = (await op.PostAsync("/api/operator/buffet-only/start", null)).EnsureSuccessStatusCode();
            var openSaleId = (await bo.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
                .GetProperty("openSaleId").GetGuid();
            await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
            await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/lines",
                new { catalogItemId = "penny", kind = "Service" });
            await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null);
            var confirm = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm",
                new { payments = new[] { new { method = "Wallet", amountToman = 1L } } });
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        }

        var (real, gift, spendable, ledger) = await WalletStateAsync(factory, customerId);
        Assert.Equal(79_992, real);
        Assert.Equal(19_998, gift);
        Assert.Equal(ledger, spendable);
    }

    [Fact]
    public async Task round3_a6_debt_opening_gift_and_prior_cash_conserve_spendable()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await MgrAsync();
        var op = await OpAsync();
        var freshId = await CreateCustomerAsync(mgr, "09123130002");
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{freshId}/opening-balance",
            new { debtToman = 10_000L, giftToman = 5_000L, lifetimeHours = 49L });
        var (r0, g0, s0, l0) = await WalletStateAsync(factory, freshId);
        Assert.Equal(-10_000, r0);
        Assert.Equal(5_000, g0);
        Assert.Equal(l0, s0);

        var priorCashId = await CreateCustomerAsync(op, "09123130003");
        await op.PostAsJsonAsync($"/api/operator/members/customers/{priorCashId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L, commandId = Guid.NewGuid()});
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{priorCashId}/opening-balance",
            new { debtToman = 10_000L });
        var (r1, _, s1, l1) = await WalletStateAsync(factory, priorCashId);
        Assert.Equal(40_000, r1);
        Assert.Equal(l1, s1);

    }

    [Fact]
    public async Task round3_c2_invoice_get_exposes_level_and_birthday_discount_lines()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await MgrAsync();
        var op = await OpAsync();
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var customerId = await CreateCustomerAsync(mgr, "09123130004");
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/opening-balance",
            new { lifetimeHours = 50L });
        await mgr.PutAsJsonAsync($"/api/operator/members/customers/{customerId}/birthday",
            new { year = 1380, month = 3, day = 13 });
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 500_000L, commandId = Guid.NewGuid()});
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-C2R3", 1);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-C2R3", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CatalogSeed.ChipsId });
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        var bill = await op.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        var collect = bill.GetProperty("collectibleToman").GetInt64();
        var confirm = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = new[] { new { method = "Cash", amountToman = collect } } });
        confirm.EnsureSuccessStatusCode();
        var body = await confirm.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var number = body.GetProperty("number").GetInt64();
        var viaHttp = await op.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/operator/invoices/{number}");
        Assert.Equal(5_500, viaHttp.GetProperty("levelDiscountToman").GetInt64());
        Assert.Equal(11_000, viaHttp.GetProperty("birthdayDiscountToman").GetInt64());
        Assert.Equal(115_000, viaHttp.GetProperty("unroundedPayableToman").GetInt64() + viaHttp.GetProperty("roundingToman").GetInt64());
    }

}
