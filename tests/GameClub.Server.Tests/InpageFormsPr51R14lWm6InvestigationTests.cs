using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

/// <summary>R14l item (4): WM6 investigation only — documents behaviour, does not change product.</summary>
[Collection("server")]
public sealed class InpageFormsPr51R14lWm6InvestigationTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task wm6_wallet_min_bill_off_zero_mixed_8000_observed_numbers()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        (await manager.PutAsJsonAsync("/api/operator/settings/rounding", new
        {
            enabled = true,
            stepToman = 5_000L,
            direction = "Up",
            applyToWallet = true,
            walletMinBillToman = 0L,
            allowWalletShortBalanceDebt = true,
        })).EnsureSuccessStatusCode();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = (await (await op.PostAsJsonAsync("/api/operator/customers", new { name = "wm6", mobile = "09186667777" }))
            .Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 8_000L });
        (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null)).EnsureSuccessStatusCode();

        var paymentsExactNet = new[]
        {
            new { method = "Wallet", amountToman = 4_000L },
            new { method = "Cash", amountToman = 4_000L },
        };
        var previewExact = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments = paymentsExactNet });
        Assert.Equal(HttpStatusCode.BadRequest, previewExact.StatusCode);

        var paymentsRounded = new[]
        {
            new { method = "Wallet", amountToman = 4_000L },
            new { method = "Cash", amountToman = 6_000L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments = paymentsRounded });
        preview.EnsureSuccessStatusCode();
        var bill = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill");
        var collectible = bill.GetProperty("collectibleToman").GetInt64();
        var unrounded = bill.GetProperty("unroundedPayableToman").GetInt64();

        var resolved = PaymentRounding.Resolve(
            unrounded,
            new RoundingPolicy(true, 5_000, RoundingDirection.Up, RoundingScope.WholeInvoice),
            new PaymentRoundingSettings
            {
                Enabled = true,
                ApplyToWallet = true,
                WalletMinBillToman = 0,
            },
            true);

        Assert.Equal(8_000, unrounded);
        Assert.False(unrounded < 0L);
        Assert.True(resolved.WalletRoundsLikeCash);
        Assert.Equal(10_000, collectible);
    }
}