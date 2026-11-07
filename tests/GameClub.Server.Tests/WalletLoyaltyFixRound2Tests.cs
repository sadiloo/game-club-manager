using System.Net;
using System.Net.Http.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class WalletLoyaltyFixRound2Tests(GameClubWebApplicationFactory factory)
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

    private static async Task<(long Spendable, long LedgerSum)> WalletTotalsAsync(
        GameClubWebApplicationFactory factory,
        Guid customerId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var member = await db.CustomerMembers.AsNoTracking().FirstAsync(m => m.CustomerId == customerId);
        var ledger = await db.WalletLedger.AsNoTracking().Where(e => e.CustomerId == customerId).ToListAsync();
        var sum = ledger.Aggregate(0L, (a, e) => a + e.RealDeltaToman + e.GiftDeltaToman);
        return (member.RealToman + member.GiftToman, sum);
    }

    [Fact]
    public async Task round2_a2_concurrent_distinct_topups_conserve_spendable()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await OpAsync();
        var op2 = await OpAsync();
        var customerId = await CreateCustomerAsync(op, "09122110001");
        async Task<HttpResponseMessage> TopUp(HttpClient c, long amount) =>
            await c.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
                new { method = "Cash", amountToman = amount });
        var responses = await RaceAsync(() => TopUp(op, 10_000), () => TopUp(op2, 20_000));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var (spendable, ledger) = await WalletTotalsAsync(factory, customerId);
        Assert.Equal(30_000, spendable);
        Assert.Equal(ledger, spendable);
    }

    [Fact]
    public async Task round2_a2_many_concurrent_topups_conserve_spendable()
    {
        await S3TestHelpers.ResetAsync(factory);
        var customerId = await CreateCustomerAsync(await OpAsync(), "09122110002");
        var clients = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ => await OpAsync()));
        var responses = await RaceAsync(clients.Select((c, i) => (Func<Task<HttpResponseMessage>>)(() =>
            c.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
                new { method = "Cash", amountToman = 1_000L }))).ToArray());
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var (spendable, ledger) = await WalletTotalsAsync(factory, customerId);
        Assert.Equal(10_000, spendable);
        Assert.Equal(ledger, spendable);
    }

    [Fact]
    public async Task round2_a4_s6_wallet_surcharge_bill_confirms()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await OpAsync();
        var customerId = await CreateCustomerAsync(op, "09122110003");
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L });
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-S6", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-S6", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/surcharge", JsonContent.Create(new { reason = (string?)null }));
        var bill = await op.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal(5_000, bill.GetProperty("surchargeToman").GetInt64());
        var collect = bill.GetProperty("collectibleToman").GetInt64();
        var walletPreview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = collect } } });
        walletPreview.EnsureSuccessStatusCode();
        var plannedCollect = (await walletPreview.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("collectibleToman").GetInt64();
        var confirm = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = plannedCollect } } });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        await S3TestHelpers.RegisterConsole(_api, op, "PS5-S6B", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var session2 = await S3TestHelpers.StartSession(_api, "PS5-S6B", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{session2}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        await op.PostAsync($"/api/operator/sessions/{session2}/bill/open", null);
        await op.PostAsync($"/api/operator/sessions/{session2}/bill/surcharge", JsonContent.Create(new { reason = (string?)null }));
        var bill2 = await op.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/operator/sessions/{session2}/bill");
        var collect2 = bill2.GetProperty("collectibleToman").GetInt64();
        var half = collect2 / 2;
        var mixedPreview = await op.PostAsJsonAsync($"/api/operator/sessions/{session2}/bill/preview",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = half },
                    new { method = "Cash", amountToman = collect2 - half },
                },
            });
        mixedPreview.EnsureSuccessStatusCode();
        var mixed = await op.PostAsJsonAsync($"/api/operator/sessions/{session2}/bill/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = half },
                    new { method = "Cash", amountToman = collect2 - half },
                },
            });
        Assert.Equal(HttpStatusCode.OK, mixed.StatusCode);
        var walletInv = await op.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/operator/invoices/1");
        Assert.Equal(0, walletInv.GetProperty("servicesGrossToman").GetInt64());
        Assert.Equal(5_000, walletInv.GetProperty("surchargeToman").GetInt64());
        var mixedInv = await op.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/operator/invoices/2");
        Assert.Equal(0, mixedInv.GetProperty("servicesGrossToman").GetInt64());
        Assert.Equal(5_000, mixedInv.GetProperty("surchargeToman").GetInt64());
        Assert.Equal(
            mixedInv.GetProperty("collectibleToman").GetInt64(),
            mixedInv.GetProperty("timeGrossToman").GetInt64()
                + mixedInv.GetProperty("buffetGrossToman").GetInt64()
                + mixedInv.GetProperty("servicesGrossToman").GetInt64()
                - mixedInv.GetProperty("discountToman").GetInt64()
                + mixedInv.GetProperty("roundingToman").GetInt64()
                + mixedInv.GetProperty("surchargeToman").GetInt64());
    }

    [Fact]
    public async Task round2_a6_opening_after_top_up_matches_ledger()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await MgrAsync();
        var op = await OpAsync();
        var customerId = await CreateCustomerAsync(op, "09122110004");
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 10_000L });
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/opening-balance",
            new { realToman = 10_000L });
        var (spendable, ledger) = await WalletTotalsAsync(factory, customerId);
        Assert.Equal(20_000, spendable);
        Assert.Equal(ledger, spendable);
    }

    [Fact]
    public async Task round2_a6_fresh_debt_and_gift_opening_matches_ledger()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await MgrAsync();
        var customerId = await CreateCustomerAsync(mgr, "09122110041");
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/opening-balance",
            new { debtToman = 10_000L, giftToman = 5_000L, lifetimeHours = 49L });
        var (spendable, ledger) = await WalletTotalsAsync(factory, customerId);
        var wallet = await mgr.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(-10_000, wallet.GetProperty("realToman").GetInt64());
        Assert.Equal(5_000, wallet.GetProperty("giftToman").GetInt64());
        Assert.Equal(ledger, spendable);
    }

    [Fact]
    public async Task round2_a6_prior_cash_then_debt_opening_matches_ledger()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await MgrAsync();
        var op = await OpAsync();
        var customerId = await CreateCustomerAsync(op, "09122110042");
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L });
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/opening-balance",
            new { debtToman = 10_000L });
        var (spendable, ledger) = await WalletTotalsAsync(factory, customerId);
        var wallet = await mgr.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(40_000, wallet.GetProperty("realToman").GetInt64());
        Assert.Equal(ledger, spendable);
    }

    [Fact]
    public async Task round2_c3_operator_package_put_forbidden()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await OpAsync();
        var put = await op.PutAsJsonAsync("/api/operator/members/packages/pkg-test",
            new { name = "t", priceToman = 1L, minutes = 120L });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    public async Task round2_c3_cash_package_sale_writes_invoice_and_payment()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await MgrAsync();
        var op = await OpAsync();
        var customerId = await CreateCustomerAsync(op, "09122110005");
        await mgr.PutAsJsonAsync("/api/operator/members/packages/pkg-cash",
            new { name = "بسته", priceToman = 50_000L, minutes = 60L });
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/packages/sell",
            new { productId = "pkg-cash", payments = new[] { new { method = "Cash", amountToman = 50_000L } } });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.Include(i => i.PaymentLines).SingleAsync(i => i.CustomerId == customerId);
        Assert.Equal(50_000, invoice.CollectibleToman);
        Assert.Equal(0, invoice.BuffetGrossToman);
        Assert.Contains(invoice.PaymentLines, p => p.Method == PaymentMethod.Cash && p.AmountToman == 50_000);
        Assert.True(await db.WalletLedger.AnyAsync(e =>
            e.CustomerId == customerId && e.Kind == WalletLedgerKind.PackagePurchase && e.InvoiceId == invoice.Id));
    }

    [Fact]
    public async Task round2_c2_discount_cap_route_configurable()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await MgrAsync();
        var get0 = await mgr.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/operator/members/settings/discount-cap");
        Assert.Equal(100, get0.GetProperty("maxCombinedPercent").GetInt32());
        var put = await mgr.PutAsJsonAsync("/api/operator/members/settings/discount-cap", new { maxCombinedPercent = 80 });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var get1 = await mgr.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/operator/members/settings/discount-cap");
        Assert.Equal(80, get1.GetProperty("maxCombinedPercent").GetInt32());
    }

    [Fact]
    public async Task round2_a2_concurrent_confirm_reject_pending_top_up_exclusive()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await OpAsync();
        var op2 = await OpAsync();
        var customerId = await CreateCustomerAsync(op, "09122110006");
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "CardToCard", amountToman = 5_000L, destinationCardId = "card-a" });
        long pendingId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            pendingId = await db.PendingWalletTopUps.Where(p => p.CustomerId == customerId).Select(p => p.Id).SingleAsync();
        }

        var responses = await RaceAsync(
            () => op.PostAsync($"/api/operator/members/wallet/pending/{pendingId}/confirm", null),
            () => op2.PostAsync($"/api/operator/members/wallet/pending/{pendingId}/reject", null));
        Assert.Equal(1, responses.Count(r => r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pending = await db.PendingWalletTopUps.AsNoTracking().SingleAsync(p => p.Id == pendingId);
            var credited = await db.WalletLedger.AnyAsync(e => e.CustomerId == customerId && e.Kind == WalletLedgerKind.TopUpReal && e.RealDeltaToman == 5_000);
            if (pending.Status == PendingWalletTopUpStatus.Rejected)
            {
                Assert.False(credited);
            }
            else
            {
                Assert.True(credited);
            }
        }
    }

    [Fact]
    public async Task round2_c4_prepaid_remaining_seconds_after_pause()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await OpAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-C4", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-C4", 1);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/prepaid/duration", new { minutes = 60 });
        factory.Clock.SetTehran(2026, 6, 3, 14, 20);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/pause", null);
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/resume", null);
        var st = await op.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/operator/sessions/{sessionId}/prepaid");
        Assert.Equal(2_400, st.GetProperty("remainingActiveSeconds").GetInt64());
    }

    [Fact]
    public async Task round2_a2_concurrent_wallet_confirms_on_shared_balance_conserve()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await OpAsync();
        var op2 = await OpAsync();
        var customerId = await CreateCustomerAsync(op, "09122110007");
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 300_000L });
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-WC1", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-WC2", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var s1 = await S3TestHelpers.StartSession(_api, "PS5-WC1", 1);
        var s2 = await S3TestHelpers.StartSession(_api, "PS5-WC2", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{s1}/customer", new { customerId });
        await op.PutAsJsonAsync($"/api/operator/sessions/{s2}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        await op.PostAsync($"/api/operator/sessions/{s1}/bill/open", null);
        await op.PostAsync($"/api/operator/sessions/{s2}/bill/open", null);
        var b1 = await op.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/operator/sessions/{s1}/bill");
        var b2 = await op.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/operator/sessions/{s2}/bill");
        var pay1 = b1.GetProperty("collectibleToman").GetInt64();
        var pay2 = b2.GetProperty("collectibleToman").GetInt64();
        var responses = await RaceAsync(
            () => op.PostAsJsonAsync($"/api/operator/sessions/{s1}/bill/confirm",
                new { payments = new[] { new { method = "Wallet", amountToman = pay1 } } }),
            () => op2.PostAsJsonAsync($"/api/operator/sessions/{s2}/bill/confirm",
                new { payments = new[] { new { method = "Wallet", amountToman = pay2 } } }));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var (spendable, ledger) = await WalletTotalsAsync(factory, customerId);
        Assert.Equal(ledger, spendable);
        Assert.Equal(300_000 - pay1 - pay2, spendable);
    }
}
