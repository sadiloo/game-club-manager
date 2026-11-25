using System.Net.Http.Json;
using System.Text.Json;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

/// <summary>R14h gate: preview/confirm parity pins for A05c balance 49,700 and X14 debt-floor.</summary>
[Collection("server")]
public sealed class InpageFormsPr51R14hGateTests(GameClubWebApplicationFactory factory)
{

    [Fact]
    public async Task r14h_a05c_buffet_balance49700_floor0_wallet49500_cash1000_preview_confirm_parity()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingUp5kAsync(manager);
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = 0L });
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenBuffetAsync(op, customerId, 49_500L);
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 49_500L },
            new { method = "Cash", amountToman = 1_000L },
        };
        await PaymentSettlementParityAssert.AssertEndpointsParityAsync(
            op,
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments },
            new { previewCollectibleToman = 50_000L, payments },
            buffetShape: true,
            factory);
    }

    [Fact]
    public async Task r14h_a05c_session_balance49700_floor0_wallet49500_cash1000_preview_confirm_parity()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingUp5kAsync(manager);
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = 0L });
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-R14H-A05C", 1);
        factory.Clock.SetTehran(2026, 6, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-R14H-A05C", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 49_500L },
            new { method = "Cash", amountToman = 1_000L },
        };
        await PaymentSettlementParityAssert.AssertEndpointsParityAsync(
            op,
            $"/api/operator/sessions/{sessionId}/bill/preview",
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments },
            new { previewCollectibleToman = 50_000L, payments },
            buffetShape: false,
            factory);
    }

    [Fact]
    public async Task r14h_x14_buffet_balance49700_debt_floor_wallet49900_cash300_preview_confirm_parity()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingUp5kAsync(manager);
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenBuffetAsync(op, customerId, 49_500L);
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 49_900L },
            new { method = "Cash", amountToman = 300L },
        };
        await PaymentSettlementParityAssert.AssertEndpointsParityAsync(
            op,
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments },
            new { previewCollectibleToman = 50_000L, payments },
            buffetShape: true,
            factory);
    }

    [Fact]
    public async Task r14h_x14_session_balance49700_debt_floor_wallet49900_cash300_preview_confirm_parity()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingUp5kAsync(manager);
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-R14H-X14", 1);
        factory.Clock.SetTehran(2026, 6, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-R14H-X14", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 49_900L },
            new { method = "Cash", amountToman = 300L },
        };
        await PaymentSettlementParityAssert.AssertEndpointsParityAsync(
            op,
            $"/api/operator/sessions/{sessionId}/bill/preview",
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments },
            new { previewCollectibleToman = 50_000L, payments },
            buffetShape: false,
            factory);
    }

    private static async Task PutRoundingUp5kAsync(HttpClient manager)
    {
        (await manager.PutAsJsonAsync("/api/operator/settings/rounding", new
        {
            enabled = true,
            stepToman = 5_000L,
            direction = "Up",
            applyToWallet = true,
            walletMinBillToman = 5_000L,
            allowWalletShortBalanceDebt = true,
        })).EnsureSuccessStatusCode();
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r14h", mobile = $"0918{Random.Shared.Next(1000000, 9999999)}" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> OpenBuffetAsync(HttpClient op, Guid customerId, long amount)
    {
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = amount });
        (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null)).EnsureSuccessStatusCode();
        return openSaleId;
    }
}