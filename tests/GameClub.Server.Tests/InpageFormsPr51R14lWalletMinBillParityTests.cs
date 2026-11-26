using System.Net.Http.Json;
using System.Text.Json;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

/// <summary>R14l: bill net exactly WalletMinBillToman uses cash-like rounding (strict &lt; at Invoicing.cs:819).</summary>
[Collection("server")]
public sealed class InpageFormsPr51R14lWalletMinBillParityTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task parity_wallet_min_bill_12000_mixed_buffet_rounds_like_cash_15000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        var opShift = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.OpenShiftAsync(opShift, 0);
        (await manager.PutAsJsonAsync("/api/operator/settings/rounding", new
        {
            enabled = true,
            stepToman = 5_000L,
            direction = "Up",
            applyToWallet = true,
            walletMinBillToman = 12_000L,
            allowWalletShortBalanceDebt = true,
        })).EnsureSuccessStatusCode();
        var op = opShift;
        var customerId = (await (await op.PostAsJsonAsync("/api/operator/customers", new { name = "r14l-wmb", mobile = "09184445555" }))
            .Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 12_000L });
        (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null)).EnsureSuccessStatusCode();
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 6_000L },
            new { method = "Cash", amountToman = 9_000L },
        };
        var previewPath = $"/api/operator/buffet-only/{openSaleId}/bill/preview";
        var confirmPath = $"/api/operator/buffet-only/{openSaleId}/confirm";
        await PaymentSettlementParityAssert.AssertEndpointsParityAsync(
            op,
            previewPath,
            confirmPath,
            new { payments },
            new { previewCollectibleToman = 15_000L, payments },
            buffetShape: true,
            factory);
    }
}