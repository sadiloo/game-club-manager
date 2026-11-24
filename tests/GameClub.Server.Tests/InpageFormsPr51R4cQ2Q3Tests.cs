using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

/// <summary>R4c Q2/Q3 owner rulings (2026-11-24): bill-level mixed total; whole invoice rounded once at/above threshold.</summary>
[Collection("server")]
public sealed class InpageFormsPr51R4cQ2Q3Tests(GameClubWebApplicationFactory factory)
{
    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "q2q3", mobile = $"0918{Random.Shared.Next(1000000, 9999999)}" });
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

    private static async Task PutRoundingAsync(HttpClient client, bool applyToWallet)
    {
        var res = await client.PutAsJsonAsync(
            "/api/operator/settings/rounding",
            new
            {
                enabled = true,
                stepToman = 5_000L,
                direction = "Up",
                applyToWallet,
                walletMinBillToman = 5_000L,
                allowWalletShortBalanceDebt = true,
            });
        res.EnsureSuccessStatusCode();
    }

    private static async Task ConfirmMixedAsync(HttpClient op, Guid openSaleId, long wallet, long cash)
    {
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = wallet },
                    new { method = "Cash", amountToman = cash },
                },
            })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task r4c_q2_server_buffet_3000_wallet_1000_cash_2000_collectible_3000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenCustomBuffetAsync(op, customerId, 3_000L);
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 1_000L },
                    new { method = "Cash", amountToman = 2_000L },
                },
            });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(3_000, BillCollectible(await preview.Content.ReadFromJsonAsync<JsonElement>()));
        var before = await WalletSpendableAsync(op, customerId);
        await ConfirmMixedAsync(op, openSaleId, 1_000, 2_000);
        Assert.Equal(before - 1_000, await WalletSpendableAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(3_000, invoice.CollectibleToman);
        Assert.Equal(0, invoice.RoundingToman);
    }

    [Fact]
    public async Task r4c_q3_server_game_custom_149850_wallet_50001_cash_99999_collectible_150000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenCustomBuffetAsync(op, customerId, 149_850L);
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 50_001L },
                    new { method = "Cash", amountToman = 99_999L },
                },
            });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(150_000, BillCollectible(await preview.Content.ReadFromJsonAsync<JsonElement>()));
        var before = await WalletSpendableAsync(op, customerId);
        await ConfirmMixedAsync(op, openSaleId, 50_001, 99_999);
        Assert.Equal(before - 50_001, await WalletSpendableAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(150_000, invoice.CollectibleToman);
        Assert.Equal(150, invoice.RoundingToman);
    }

    [Fact]
    public async Task r4c_preview_server_buffet_12000_wallet_1000_only_collectible_15000()
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
            new { payments = new[] { new { method = "Wallet", amountToman = 1_000L } } });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(15_000, BillCollectible(await preview.Content.ReadFromJsonAsync<JsonElement>()));
    }

    [Fact]
    public async Task r4c_preview_server_buffet_149850_wallet_50001_only_collectible_150000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenCustomBuffetAsync(op, customerId, 149_850L);
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 50_001L } } });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(150_000, BillCollectible(await preview.Content.ReadFromJsonAsync<JsonElement>()));
    }

    [Fact]
    public async Task r4c_preview_server_buffet_3000_wallet_1000_only_collectible_3000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenCustomBuffetAsync(op, customerId, 3_000L);
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 1_000L } } });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(3_000, BillCollectible(await preview.Content.ReadFromJsonAsync<JsonElement>()));
    }

    [Fact]
    public async Task r4c_q2_server_underpay_3000_wallet_1000_cash_1999_rejected()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var before = await WalletSpendableAsync(op, customerId);
        var openSaleId = await OpenCustomBuffetAsync(op, customerId, 3_000L);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 1_000L },
                    new { method = "Cash", amountToman = 1_999L },
                },
            });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal(before, await WalletSpendableAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task r4c_q3_server_underpay_149850_wallet_50001_cash_99998_rejected()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var before = await WalletSpendableAsync(op, customerId);
        var openSaleId = await OpenCustomBuffetAsync(op, customerId, 149_850L);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 50_001L },
                    new { method = "Cash", amountToman = 99_998L },
                },
            });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal(before, await WalletSpendableAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task r4c_q3_server_session_time_149850_wallet_50001_cash_99999_collectible_150000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();

        await S3TestHelpers.RegisterConsole(api, op, "PS5-Q3-149850", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-Q3-149850", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 58_183L })).EnsureSuccessStatusCode();
        var bill = await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        bill.EnsureSuccessStatusCode();
        S3TestHelpers.AssertMoney(await bill.Content.ReadFromJsonAsync<JsonElement>(), 149_850, 150_000);

        var preview = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/preview",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 50_001L },
                    new { method = "Cash", amountToman = 99_999L },
                },
            });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(150_000, (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("collectibleToman").GetInt64());

        var before = await WalletSpendableAsync(op, customerId);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 50_001L },
                    new { method = "Cash", amountToman = 99_999L },
                },
            })).EnsureSuccessStatusCode();
        Assert.Equal(before - 50_001, await WalletSpendableAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(150_000, invoice.CollectibleToman);
    }

    [Fact]
    public async Task q_b_ruled_apply_to_wallet_off_server_buffet_12000_mixed_preview_confirm_15000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingAsync(manager, applyToWallet: false);
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
                    new { method = "Wallet", amountToman = 6_000L },
                    new { method = "Cash", amountToman = 9_000L },
                },
            });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(15_000, BillCollectible(await preview.Content.ReadFromJsonAsync<JsonElement>()));
        var before = await WalletSpendableAsync(op, customerId);
        await ConfirmMixedAsync(op, openSaleId, 6_000, 9_000);
        Assert.Equal(before - 6_000, await WalletSpendableAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(15_000, invoice.CollectibleToman);
        Assert.Equal(3_000, invoice.RoundingToman);
    }

    [Fact]
    public async Task gate_pin_wl_04_session_preview_collectible_equals_confirm_invoice_150000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();

        await S3TestHelpers.RegisterConsole(api, op, "PS5-WL04", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-WL04", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 58_183L })).EnsureSuccessStatusCode();
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();

        var payments = new[]
        {
            new { method = "Wallet", amountToman = 50_001L },
            new { method = "Cash", amountToman = 99_999L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(150_000, previewCollectible);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = previewCollectible, payments });
        confirm.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(previewCollectible, invoice.CollectibleToman);
        Assert.Equal(150_000, invoice.CollectibleToman);
    }

    [Fact]
    public async Task q_b_ruled_apply_to_wallet_off_server_buffet_12000_wallet_only_preview_15000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingAsync(manager, applyToWallet: false);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenCustomBuffetAsync(op, customerId, 12_000L);
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 1_000L } } });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(15_000, BillCollectible(await preview.Content.ReadFromJsonAsync<JsonElement>()));
    }
}