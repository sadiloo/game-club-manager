using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsPr51R4cWalletThresholdTests(GameClubWebApplicationFactory factory)
{
    private static readonly RoundingPolicy Step5Up = RoundingPolicy.Create(5_000, RoundingDirection.Up);

    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "thr", mobile = $"0917{Random.Shared.Next(1000000, 9999999)}" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static long BillCollectible(JsonElement previewBody)
        => previewBody.GetProperty("bill").GetProperty("collectibleToman").GetInt64();

    private static async Task<long> WalletSpendableAsync(HttpClient op, Guid customerId)
    {
        var w = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        return w.GetProperty("spendableToman").GetInt64();
    }

    private static async Task<Guid> OpenCustomBuffetAsync(HttpClient op, Guid customerId, long customToman)
    {
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        start.EnsureSuccessStatusCode();
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = customToman });
        await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null);
        return openSaleId;
    }

    private static long CashCollectible(long netToman)
    {
        var request = new InvoiceRequest(
            [],
            [new BuffetLine("custom", "خدمت", 1, netToman)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            new DateTimeOffset(2026, 6, 3, 15, 0, 0, TehranTime.Offset));
        return InvoiceCalculator.Calculate(request).PayableToman;
    }

    [Fact]
    public async Task gate_pin_d4_buffet_wallet_4999_confirm_200()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenCustomBuffetAsync(op, customerId, 4_999L);
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 4_999L } } });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(4_999, BillCollectible(await preview.Content.ReadFromJsonAsync<JsonElement>()));
        var before = await WalletSpendableAsync(op, customerId);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 4_999L } } })).EnsureSuccessStatusCode();
        Assert.Equal(before - 4_999, await WalletSpendableAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(4_999, invoice.CollectibleToman);
    }

    [Theory]
    [InlineData(5_000L)]
    [InlineData(5_001L)]
    public async Task buffet_wallet_at_or_over_threshold_preview_matches_cash_collectible(long net)
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var expected = CashCollectible(net);
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenCustomBuffetAsync(op, customerId, net);
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = net } } });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(expected, BillCollectible(await preview.Content.ReadFromJsonAsync<JsonElement>()));
        var before = await WalletSpendableAsync(op, customerId);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = net } } })).EnsureSuccessStatusCode();
        Assert.Equal(before - expected, await WalletSpendableAsync(op, customerId));
        await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 200_000L,
            actualCardReaderToman = 0L,
        });
        var income = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=6&day=3");
        Assert.Equal(200_000, income.GetProperty("receivedCashToman").GetInt64());
    }

    [Theory]
    [InlineData(3_000L, 5_000L)]
    [InlineData(4_999L, 5_000L)]
    public async Task member_buffet_cash_only_under_threshold_matches_main_rounded_collectible(long net, long expectedCollectible)
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        var openSaleId = await OpenCustomBuffetAsync(op, customerId, net);
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments = new[] { new { method = "Cash", amountToman = net } } });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(expectedCollectible, BillCollectible(await preview.Content.ReadFromJsonAsync<JsonElement>()));
        var shortCash = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Cash", amountToman = net } } });
        Assert.Equal(HttpStatusCode.BadRequest, shortCash.StatusCode);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Cash", amountToman = expectedCollectible } } })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task buffet_mixed_5000_wallet_1000_cash_4000_collectible_5000_threshold_guard()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenCustomBuffetAsync(op, customerId, 5_000L);
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 1_000L },
                    new { method = "Cash", amountToman = 4_000L },
                },
            });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(5_000, BillCollectible(await preview.Content.ReadFromJsonAsync<JsonElement>()));
        var before = await WalletSpendableAsync(op, customerId);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 1_000L },
                    new { method = "Cash", amountToman = 4_000L },
                },
            })).EnsureSuccessStatusCode();
        Assert.Equal(before - 1_000, await WalletSpendableAsync(op, customerId));
    }

    [Fact]
    public async Task buffet_mixed_5000_wallet_1000_cash_5000_rejected_wallet_uncharged()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var before = await WalletSpendableAsync(op, customerId);
        var openSaleId = await OpenCustomBuffetAsync(op, customerId, 5_000L);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 1_000L },
                    new { method = "Cash", amountToman = 5_000L },
                },
            });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal(before, await WalletSpendableAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task buffet_mixed_12000_wallet_1000_cash_14000_accepted_collectible_15000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenCustomBuffetAsync(op, customerId, 12_000L);
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 1_000L },
                    new { method = "Cash", amountToman = 14_000L },
                },
            });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(15_000, BillCollectible(await preview.Content.ReadFromJsonAsync<JsonElement>()));
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 1_000L },
                    new { method = "Cash", amountToman = 14_000L },
                },
            })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task buffet_mixed_12000_wallet_1000_cash_15000_rejected_wallet_uncharged()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var before = await WalletSpendableAsync(op, customerId);
        var openSaleId = await OpenCustomBuffetAsync(op, customerId, 12_000L);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 1_000L },
                    new { method = "Cash", amountToman = 15_000L },
                },
            });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal(before, await WalletSpendableAsync(op, customerId));
    }
}