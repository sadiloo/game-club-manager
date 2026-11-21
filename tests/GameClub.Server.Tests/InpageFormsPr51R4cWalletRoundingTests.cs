using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

/// <summary>PR51 R4c: full wallet rounds like cash; short balance finishes via cash or debt.</summary>
[Collection("server")]
public sealed class InpageFormsPr51R4cWalletRoundingTests(GameClubWebApplicationFactory factory)
{
    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r4c", mobile = $"0916{Random.Shared.Next(1000000, 9999999)}" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<long> WalletSpendableAsync(HttpClient op, Guid customerId)
    {
        var w = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        return w.GetProperty("spendableToman").GetInt64();
    }

    private static async Task<Guid> Buffet49500OpenAsync(HttpClient op, Guid customerId)
    {
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        start.EnsureSuccessStatusCode();
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null);
        return openSaleId;
    }

    [Fact]
    public async Task session_full_wallet_91667_typed_draws_95000_like_cash()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();

        await S3TestHelpers.RegisterConsole(api, op, "PS5-R4C91667", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-R4C91667", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        var bill = await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        bill.EnsureSuccessStatusCode();
        S3TestHelpers.AssertMoney(await bill.Content.ReadFromJsonAsync<JsonElement>(), 91_667, 95_000);

        var before = await WalletSpendableAsync(op, customerId);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 91_667L } } })).EnsureSuccessStatusCode();
        Assert.Equal(before - 95_000, await WalletSpendableAsync(op, customerId));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(91_667, invoice.UnroundedPayableToman);
        Assert.Equal(3_333, invoice.RoundingToman);
        Assert.Equal(95_000, invoice.CollectibleToman);
    }

    [Fact]
    public async Task buffet_wallet_balance_49700_plus_cash_300_meets_collectible()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();

        var openSaleId = await Buffet49500OpenAsync(op, customerId);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 49_700L },
                    new { method = "Cash", amountToman = 300L },
                },
            })).EnsureSuccessStatusCode();

        var preview = await op.GetFromJsonAsync<JsonElement>("/api/operator/shift/close/preview");
        Assert.Equal(50_000, preview.GetProperty("expected").GetProperty("cashExpected").GetInt64());
        (await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 50_000L,
            actualCardReaderToman = 0L,
        })).EnsureSuccessStatusCode();

        var income = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=6&day=3");
        Assert.Equal(50_000, income.GetProperty("receivedMoneyToman").GetInt64());
        Assert.Equal(50_000, income.GetProperty("receivedCashToman").GetInt64());
    }

    [Fact]
    public async Task buffet_wallet_balance_49700_debt_floor_covers_rounding_gap()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();

        var openSaleId = await Buffet49500OpenAsync(op, customerId);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 50_000L } } })).EnsureSuccessStatusCode();
        Assert.Equal(-300, await WalletSpendableAsync(op, customerId));
    }

    [Fact]
    public async Task buffet_wallet_balance_exactly_50000_full_draw_no_cash_remainder()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();

        var openSaleId = await Buffet49500OpenAsync(op, customerId);
        var before = await WalletSpendableAsync(op, customerId);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 50_000L } } })).EnsureSuccessStatusCode();
        Assert.Equal(before - 50_000, await WalletSpendableAsync(op, customerId));
    }

    [Fact]
    public async Task wallet_pay_with_gift_credit_does_not_increase_received_money()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        (await manager.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/gift",
            new { giftToman = 50_000L })).EnsureSuccessStatusCode();

        var openSaleId = await Buffet49500OpenAsync(op, customerId);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 50_000L } } })).EnsureSuccessStatusCode();

        await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 50_000L,
            actualCardReaderToman = 0L,
        });
        var income = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=6&day=3");
        Assert.Equal(50_000, income.GetProperty("receivedMoneyToman").GetInt64());
        Assert.Equal(50_000, income.GetProperty("receivedCashToman").GetInt64());

        using var scope = factory.Services.CreateScope();
        var effect = await scope.ServiceProvider.GetRequiredService<AppDbContext>().InvoiceMemberEffects.AsNoTracking().SingleAsync();
        Assert.True(effect.WalletGiftDrawnToman > 0);
    }
}