using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

/// <summary>PR51 R4b: wallet tender uses the same rounded collectible as cash.</summary>
[Collection("server")]
public sealed class InpageFormsPr51R4bWalletRoundingTests(GameClubWebApplicationFactory factory)
{
    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "rnd", mobile = $"0915{Random.Shared.Next(1000000, 9999999)}" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<long> WalletSpendableAsync(HttpClient op, Guid customerId)
    {
        var w = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        return w.GetProperty("spendableToman").GetInt64();
    }

    [Fact]
    public async Task wallet_typed_49500_on_49500_buffet_draws_50000_when_balance_60000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();

        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        start.EnsureSuccessStatusCode();
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null);

        var before = await WalletSpendableAsync(op, customerId);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 49_500L } } })).EnsureSuccessStatusCode();
        Assert.Equal(before - 50_000, await WalletSpendableAsync(op, customerId));
    }

    [Fact]
    public async Task wallet_pay_on_49500_buffet_bill_deducts_50000_same_rounding_as_cash()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        Assert.Equal(100_000, await WalletSpendableAsync(op, customerId));

        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        start.EnsureSuccessStatusCode();
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null);
        var detail = await op.GetFromJsonAsync<JsonElement>($"/api/operator/buffet-only/{openSaleId}");
        var bill = detail.GetProperty("bill");
        Assert.Equal(49_500, bill.GetProperty("unroundedPayableToman").GetInt64());
        Assert.Equal(500, bill.GetProperty("roundingToman").GetInt64());
        Assert.Equal(50_000, bill.GetProperty("collectibleToman").GetInt64());

        var beforeSpend = await WalletSpendableAsync(op, customerId);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 50_000L } } })).EnsureSuccessStatusCode();
        Assert.Equal(beforeSpend - 50_000, await WalletSpendableAsync(op, customerId));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(49_500, invoice.UnroundedPayableToman);
        Assert.Equal(500, invoice.RoundingToman);
        Assert.Equal(50_000, invoice.CollectibleToman);
        var effect = await db.InvoiceMemberEffects.AsNoTracking().SingleAsync(e => e.InvoiceId == invoice.Id);
        Assert.Equal(50_000, effect.WalletRealDrawnToman + effect.WalletGiftDrawnToman);
    }

    [Fact]
    public async Task mixed_wallet_and_cash_uses_single_invoice_rounding_not_per_tender()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();

        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        start.EnsureSuccessStatusCode();
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 149_850L });
        await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null);
        var detail = await op.GetFromJsonAsync<JsonElement>($"/api/operator/buffet-only/{openSaleId}");
        var collectible = detail.GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        Assert.Equal(150_000, collectible);

        var before = await WalletSpendableAsync(op, customerId);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 50_000L },
                    new { method = "Cash", amountToman = 100_000L },
                },
            })).EnsureSuccessStatusCode();
        Assert.Equal(before - 50_000, await WalletSpendableAsync(op, customerId));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(150_000, invoice.CollectibleToman);
        Assert.Equal(150, invoice.RoundingToman);
    }

    [Fact]
    public async Task session_time_only_full_wallet_49500_matches_cash_twin_collectible_50000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();

        await S3TestHelpers.RegisterConsole(api, op, "PS5-RND495", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-RND495", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 27);
        var open = await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        open.EnsureSuccessStatusCode();
        var bill = await open.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(49_500, bill!.GetProperty("unroundedPayableToman").GetInt64());
        Assert.Equal(50_000, bill.GetProperty("collectibleToman").GetInt64());

        var cashPreview = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/preview",
            new { payments = new[] { new { method = "Cash", amountToman = 50_000L } } });
        cashPreview.EnsureSuccessStatusCode();
        Assert.Equal(50_000, (await cashPreview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64());

        var walletPreview = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 50_000L } } });
        walletPreview.EnsureSuccessStatusCode();
        Assert.Equal(50_000, (await walletPreview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64());

        var before = await WalletSpendableAsync(op, customerId);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 50_000L } } })).EnsureSuccessStatusCode();
        Assert.Equal(before - 50_000, await WalletSpendableAsync(op, customerId));
    }
}