using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsPr51R5SessionScenariosTests(GameClubWebApplicationFactory factory)
{
    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static void AssertPaymentInvalid(JsonElement body)
    {
        Assert.Equal("payment_invalid", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r5s", mobile = $"0916{Random.Shared.Next(1000000, 9999999)}" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<long> WalletSpendableAsync(HttpClient op, Guid customerId)
    {
        var w = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        return w.GetProperty("spendableToman").GetInt64();
    }

    private async Task<Guid> SessionBuffet49500BillOpenAsync(
        HttpClient api,
        HttpClient op,
        Guid customerId,
        string stationId)
    {
        await S3TestHelpers.RegisterConsole(api, op, stationId, 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, stationId, 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        return sessionId;
    }

    private static async Task AssertSessionPaymentLinesAsync(
        GameClubWebApplicationFactory factory,
        long expectedWalletLineToman,
        long expectedCollectibleToman)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking()
            .Include(i => i.PaymentLines)
            .SingleAsync();
        Assert.Equal(expectedCollectibleToman, invoice.CollectibleToman);
        var walletLine = invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Wallet);
        var cashLine = invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Cash);
        Assert.Equal(expectedWalletLineToman, walletLine.AmountToman);
        Assert.Equal(expectedWalletLineToman, walletLine.WalletRealDrawnToman + walletLine.WalletGiftDrawnToman);
        Assert.Equal(expectedCollectibleToman, walletLine.AmountToman + cashLine.AmountToman);
        var spend = await db.WalletLedger.AsNoTracking()
            .Where(w => w.Kind == WalletLedgerKind.InvoiceSpend)
            .SingleAsync();
        Assert.Equal(-walletLine.WalletRealDrawnToman, spend.RealDeltaToman);
    }

    [Fact]
    public async Task f05_scenario_s15_short_wallet_floor_debt_mixed_49600_cash_400_accepted()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        start.EnsureSuccessStatusCode();
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null)).EnsureSuccessStatusCode();
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 49_600L },
                    new { method = "Cash", amountToman = 400L },
                },
            })).EnsureSuccessStatusCode();
        Assert.Equal(100, await WalletSpendableAsync(op, customerId));
        await AssertBuffetPaymentLinesAsync(factory, 49_600, 50_000);
    }

    [Fact]
    public async Task f05_scenario_s16_short_wallet_floor_debt_mixed_49500_cash_500_accepted()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        start.EnsureSuccessStatusCode();
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null)).EnsureSuccessStatusCode();
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 49_500L },
                    new { method = "Cash", amountToman = 500L },
                },
            })).EnsureSuccessStatusCode();
        Assert.Equal(200, await WalletSpendableAsync(op, customerId));
        await AssertBuffetPaymentLinesAsync(factory, 49_500, 50_000);
    }

    private static async Task AssertBuffetPaymentLinesAsync(
        GameClubWebApplicationFactory factory,
        long expectedWalletLineToman,
        long expectedCollectibleToman)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking()
            .Include(i => i.PaymentLines)
            .SingleAsync();
        Assert.Equal(expectedCollectibleToman, invoice.CollectibleToman);
        var walletLine = invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Wallet);
        var cashLine = invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Cash);
        Assert.Equal(expectedWalletLineToman, walletLine.AmountToman);
        Assert.Equal(expectedWalletLineToman, walletLine.WalletRealDrawnToman + walletLine.WalletGiftDrawnToman);
        Assert.Equal(expectedCollectibleToman, walletLine.AmountToman + cashLine.AmountToman);
    }

    [Fact]
    public async Task f05_scenario_ss5_session_wrong_split_floor_zero_rejected_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = 0L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var before = await WalletSpendableAsync(op, customerId);
        var sessionId = await SessionBuffet49500BillOpenAsync(api, op, customerId, "PS5-SS5");
        var body = new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 49_500L },
                new { method = "Cash", amountToman = 500L },
            },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", body);
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        AssertPaymentInvalid(await ReadJsonAsync(preview));
        var confirm = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", body);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        AssertPaymentInvalid(await ReadJsonAsync(confirm));
        Assert.Equal(before, await WalletSpendableAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task f05_scenario_sse_session_s7_over_tender_preview_collectible_confirm_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var sessionId = await SessionBuffet49500BillOpenAsync(api, op, customerId, "PS5-SSE7");
        var body = new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 49_500L },
                new { method = "Cash", amountToman = 1_000L },
            },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", body);
        preview.EnsureSuccessStatusCode();
        var previewJson = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(50_000, previewJson!.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(500, previewJson.GetProperty("changeToman").GetInt64());
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = 50_000L, payments = body.payments })).EnsureSuccessStatusCode();
        await AssertSessionPaymentLinesAsync(factory, 49_500, 50_000);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cash = await db.PaymentLines.AsNoTracking().SingleAsync(p => p.Method == PaymentMethod.Cash);
            Assert.Equal(500, cash.AmountToman);
        }
    }

    [Fact]
    public async Task f05_scenario_s2_session_mixed_wallet_debit_matches_line()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = 0L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var sessionId = await SessionBuffet49500BillOpenAsync(api, op, customerId, "PS5-S2");
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 49_500L },
                    new { method = "Cash", amountToman = 500L },
                },
            })).EnsureSuccessStatusCode();
        Assert.Equal(10_500, await WalletSpendableAsync(op, customerId));
        await AssertSessionPaymentLinesAsync(factory, 49_500, 50_000);
    }

    [Fact]
    public async Task f05_scenario_s4_session_mixed_wallet_49700_cash_300()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var sessionId = await SessionBuffet49500BillOpenAsync(api, op, customerId, "PS5-S4");
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 49_700L },
                    new { method = "Cash", amountToman = 300L },
                },
            })).EnsureSuccessStatusCode();
        Assert.Equal(0, await WalletSpendableAsync(op, customerId));
        await AssertSessionPaymentLinesAsync(factory, 49_700, 50_000);
    }

    [Fact]
    public async Task f05_scenario_s9_session_mixed_wallet_49600_cash_400_spendable_10400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = 0L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var sessionId = await SessionBuffet49500BillOpenAsync(api, op, customerId, "PS5-S9");
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 49_600L },
                    new { method = "Cash", amountToman = 400L },
                },
            })).EnsureSuccessStatusCode();
        Assert.Equal(10_400, await WalletSpendableAsync(op, customerId));
        await AssertSessionPaymentLinesAsync(factory, 49_600, 50_000);
    }
}