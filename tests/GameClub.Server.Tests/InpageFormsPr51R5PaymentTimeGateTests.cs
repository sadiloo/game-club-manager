using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using GameClub.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsPr51R5PaymentTimeGateTests(GameClubWebApplicationFactory factory)
{
    private static async Task PutRoundingAsync(HttpClient client, object body)
    {
        var res = await client.PutAsJsonAsync("/api/operator/settings/rounding", body);
        res.EnsureSuccessStatusCode();
    }

    private static object RoundingBody(
        bool? enabled = true,
        long? stepToman = 5_000L,
        string? direction = "Up",
        bool allowWalletShortBalanceDebt = true)
        => new
        {
            enabled = enabled ?? true,
            stepToman = stepToman ?? 5_000L,
            direction = direction ?? "Up",
            applyToWallet = true,
            walletMinBillToman = 5_000L,
            allowWalletShortBalanceDebt,
        };

    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "ptg", mobile = $"0918{Random.Shared.Next(1000000, 9999999)}" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<long> WalletSpendableAsync(HttpClient op, Guid customerId)
    {
        var w = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        return w.GetProperty("spendableToman").GetInt64();
    }

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

    private static async Task<Guid> BuffetCheckoutAsync(HttpClient op, Guid customerId, long customAmountToman)
    {
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        start.EnsureSuccessStatusCode();
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman });
        (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null)).EnsureSuccessStatusCode();
        return openSaleId;
    }

    private async Task<Guid> SessionBillOpenAsync(
        HttpClient api,
        HttpClient op,
        Guid customerId,
        string stationId = "ps5-ptg",
        long customAmountToman = 49_500L)
    {
        await S3TestHelpers.RegisterConsole(api, op, stationId, 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, stationId, 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman });
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        return sessionId;
    }

    [Fact]
    public async Task gate_over_tender_unchanged_preview_buffet_confirm_400_not_stale_rounding()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetCheckoutAsync(op, customerId, 49_500L);
        var body = new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 49_500L },
                new { method = "Cash", amountToman = 1_000L },
            },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", body);
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        Assert.Equal(50_000, previewCollectible);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                previewCollectibleToman = previewCollectible,
                payments = body.payments,
            })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.PaymentLines).SingleAsync();
        Assert.Equal(50_000, invoice.CollectibleToman);
        Assert.Equal(50_000, invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Wallet).AmountToman);
        Assert.Equal(0, invoice.PaymentLines.Count(p => p.Method == PaymentMethod.Cash));
    }

    [Fact]
    public async Task gate_over_tender_unchanged_preview_session_confirm_400_not_stale_rounding()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var sessionId = await SessionBillOpenAsync(api, op, customerId, "ps5-oter");
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
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(50_000, previewCollectible);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new
            {
                previewCollectibleToman = previewCollectible,
                payments = body.payments,
            })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        Assert.Equal(50_000, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().Select(i => i.CollectibleToman).SingleAsync());
    }

    [Fact]
    public async Task gate_mutant_cb_session_w03_wallet49000_preview_and_confirm_400_payment_invalid()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-CB-W03", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-CB-W03", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 27);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        var payments = new[] { new { method = "Wallet", amountToman = 49_000L } };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        var previewBody = await ReadJsonAsync(preview);
        AssertPaymentInvalid(previewBody);
        var confirm = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new { payments });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        var confirmBody = await ReadJsonAsync(confirm);
        AssertPaymentInvalid(confirmBody);
        Assert.Equal(previewBody.GetProperty("message").GetString(), confirmBody.GetProperty("message").GetString());
    }

    [Fact]
    public async Task gate_mutant_cb_buffet_w03_preview_200_collectible_50000_wallet_49500_confirm_matches_preview_lift()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var before = await WalletSpendableAsync(op, customerId);
        var openSaleId = await BuffetCheckoutAsync(op, customerId, 49_500L);
        var payments = new[] { new { method = "Wallet", amountToman = 49_500L } };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        Assert.Equal(50_000, previewCollectible);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { previewCollectibleToman = previewCollectible, payments });
        confirm.EnsureSuccessStatusCode();
        var confirmCollectible = (await confirm.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(previewCollectible, confirmCollectible);
        Assert.Equal(before - 50_000, await WalletSpendableAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task gate_mutant_cb_member_bill_composer_preview_400_on_buffet_invalid_allocation_fails_w03_w04()
    {
        await gate_mutant_cb_buffet_w03_preview_200_collectible_50000_wallet_49500_confirm_matches_preview_lift();
    }

    [Fact]
    public async Task gate_mutant_b2_buffet_confirm_live_prep_rejects_s5_after_step_up_at_payment()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = 0L });
        await PutRoundingAsync(manager, RoundingBody(stepToman: 1_000L, direction: "Down"));
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var before = await WalletSpendableAsync(op, customerId);
        var openSaleId = await BuffetCheckoutAsync(op, customerId, 49_500L);
        await PutRoundingAsync(manager, RoundingBody(stepToman: 5_000L, direction: "Up"));
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 49_500L },
                    new { method = "Cash", amountToman = 500L },
                },
            });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        AssertPaymentInvalid(await ReadJsonAsync(confirm));
        Assert.Equal(before, await WalletSpendableAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task gate_mutant_b2_session_confirm_live_prep_rejects_s5_after_step_up_at_payment()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = 0L });
        await PutRoundingAsync(manager, RoundingBody(stepToman: 1_000L, direction: "Down"));
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var before = await WalletSpendableAsync(op, customerId);
        var sessionId = await SessionBillOpenAsync(api, op, customerId, "ps5-b2");
        await PutRoundingAsync(manager, RoundingBody(stepToman: 5_000L, direction: "Up"));
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 49_500L },
                    new { method = "Cash", amountToman = 500L },
                },
            });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        AssertPaymentInvalid(await ReadJsonAsync(confirm));
        Assert.Equal(before, await WalletSpendableAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task gate_mutant_b4_buffet_preview_collectible_matches_confirm_after_rounding_off()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetCheckoutAsync(op, customerId, 49_500L);
        await PutRoundingAsync(manager, RoundingBody(enabled: false));
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 49_500L } } });
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        Assert.Equal(49_500, previewCollectible);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                previewCollectibleToman = previewCollectible,
                payments = new[] { new { method = "Wallet", amountToman = 49_500L } },
            })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        Assert.Equal(49_500, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().Select(i => i.CollectibleToman).SingleAsync());
    }

    [Fact]
    public async Task gate_mutant_b4_buffet_over_tender_preview_collectible_50000_not_stale_49500()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetCheckoutAsync(op, customerId, 49_500L);
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 55_000L } } });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(50_000, (await preview.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("bill").GetProperty("collectibleToman").GetInt64());
    }

    [Fact]
    public async Task gate_mutant_b4_session_preview_collectible_matches_confirm_after_rounding_off()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var sessionId = await SessionBillOpenAsync(api, op, customerId, "ps5-b4a");
        await PutRoundingAsync(manager, RoundingBody(enabled: false));
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 49_500L } } });
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(49_500, previewCollectible);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new
            {
                previewCollectibleToman = previewCollectible,
                payments = new[] { new { method = "Wallet", amountToman = 49_500L } },
            })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task gate_mutant_b4_session_over_tender_preview_collectible_50000_not_stale_49500()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var sessionId = await SessionBillOpenAsync(api, op, customerId, "ps5-b4b");
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 50_000L } } });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(50_000, (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64());
    }

    [Fact]
    public async Task gate_mutant_b4_buffet_l3_preview_confirm_50000_after_step_down_open_step_up()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = 0L });
        await PutRoundingAsync(manager, RoundingBody(stepToman: 1_000L, direction: "Down"));
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetCheckoutAsync(op, customerId, 49_000L);
        await PutRoundingAsync(manager, RoundingBody(stepToman: 5_000L, direction: "Up"));
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 49_700L },
            new { method = "Cash", amountToman = 300L },
        };
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments });
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        Assert.Equal(50_000, previewCollectible);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { previewCollectibleToman = previewCollectible, payments })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        Assert.Equal(50_000, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().Select(i => i.CollectibleToman).SingleAsync());
    }

    [Fact]
    public async Task gate_mutant_b4_session_l4_preview_confirm_50000_after_step_down_open_step_up()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = 0L });
        await PutRoundingAsync(manager, RoundingBody(stepToman: 1_000L, direction: "Down"));
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var sessionId = await SessionBillOpenAsync(api, op, customerId, "ps5-l4", customAmountToman: 49_000L);
        await PutRoundingAsync(manager, RoundingBody(stepToman: 5_000L, direction: "Up"));
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 49_700L },
            new { method = "Cash", amountToman = 300L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(50_000, previewCollectible);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = previewCollectible, payments })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        Assert.Equal(50_000, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().Select(i => i.CollectibleToman).SingleAsync());
    }

    [Fact]
    public async Task gate_mutant_b4_buffet_l7_explicit_cash_preview_confirm_50000_after_step_down_open_step_up()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        await PutRoundingAsync(manager, RoundingBody(stepToman: 1_000L, direction: "Down"));
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetCheckoutAsync(op, customerId, 49_000L);
        await PutRoundingAsync(manager, RoundingBody(stepToman: 5_000L, direction: "Up"));
        const string json = """
            {"payments":[
            {"method":"Wallet","amountToman":49700,"shortRemainderMethod":"Cash"},
            {"method":"Cash","amountToman":300}]}
            """;
        var preview = await op.PostAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new StringContent(json, Encoding.UTF8, "application/json"));
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        Assert.Equal(50_000, previewCollectible);
        var confirmJson = $$"""
            {"previewCollectibleToman":{{previewCollectible}},"payments":[
            {"method":"Wallet","amountToman":49700,"shortRemainderMethod":"Cash"},
            {"method":"Cash","amountToman":300}]}
            """;
        (await op.PostAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new StringContent(confirmJson, Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        Assert.Equal(50_000, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().Select(i => i.CollectibleToman).SingleAsync());
    }

    [Fact]
    public async Task gate_mutant_b4_session_l8_explicit_cash_preview_confirm_50000_after_step_down_open_step_up()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        await PutRoundingAsync(manager, RoundingBody(stepToman: 1_000L, direction: "Down"));
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var sessionId = await SessionBillOpenAsync(api, op, customerId, "ps5-l8", customAmountToman: 49_000L);
        await PutRoundingAsync(manager, RoundingBody(stepToman: 5_000L, direction: "Up"));
        const string json = """
            {"payments":[
            {"method":"Wallet","amountToman":49700,"shortRemainderMethod":"Cash"},
            {"method":"Cash","amountToman":300}]}
            """;
        var preview = await op.PostAsync(
            $"/api/operator/sessions/{sessionId}/bill/preview",
            new StringContent(json, Encoding.UTF8, "application/json"));
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(50_000, previewCollectible);
        var confirmJson = $$"""
            {"previewCollectibleToman":{{previewCollectible}},"payments":[
            {"method":"Wallet","amountToman":49700,"shortRemainderMethod":"Cash"},
            {"method":"Cash","amountToman":300}]}
            """;
        (await op.PostAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new StringContent(confirmJson, Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        Assert.Equal(50_000, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().Select(i => i.CollectibleToman).SingleAsync());
    }

    [Fact]
    public async Task gate_mutant_b4_buffet_t07_preview_50000_over_tender_after_rounding_enabled_at_payment()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await PutRoundingAsync(manager, RoundingBody(enabled: false));
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetCheckoutAsync(op, customerId, 49_500L);
        await PutRoundingAsync(manager, RoundingBody(enabled: true));
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 49_500L },
                    new { method = "Cash", amountToman = 1_000L },
                },
            });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(50_000, (await preview.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("bill").GetProperty("collectibleToman").GetInt64());
    }

    [Fact]
    public async Task gate_mutant_b4_session_t08_preview_50000_over_tender_after_rounding_enabled_at_payment()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await PutRoundingAsync(manager, RoundingBody(enabled: false));
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var sessionId = await SessionBillOpenAsync(api, op, customerId, "ps5-t08");
        await PutRoundingAsync(manager, RoundingBody(enabled: true));
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/preview",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 49_500L },
                    new { method = "Cash", amountToman = 1_000L },
                },
            });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(50_000, (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64());
    }

    [Fact]
    public async Task gate_mutant_g2_buffet_x11_explicit_cash_wrong_wallet_preview_and_confirm_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var before = await WalletSpendableAsync(op, customerId);
        var openSaleId = await BuffetCheckoutAsync(op, customerId, 49_500L);
        const string json = """
            {"payments":[
            {"method":"Wallet","amountToman":49000,"shortRemainderMethod":"Cash"},
            {"method":"Cash","amountToman":300}]}
            """;
        var preview = await op.PostAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        AssertPaymentInvalid(await ReadJsonAsync(preview));
        var confirm = await op.PostAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        AssertPaymentInvalid(await ReadJsonAsync(confirm));
        Assert.Equal(before, await WalletSpendableAsync(op, customerId));
    }

    [Fact]
    public async Task gate_mutant_g2_session_x13_explicit_cash_wrong_wallet_preview_and_confirm_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var before = await WalletSpendableAsync(op, customerId);
        var sessionId = await SessionBillOpenAsync(api, op, customerId, "ps5-x13");
        const string json = """
            {"payments":[
            {"method":"Wallet","amountToman":49000,"shortRemainderMethod":"Cash"},
            {"method":"Cash","amountToman":300}]}
            """;
        var preview = await op.PostAsync(
            $"/api/operator/sessions/{sessionId}/bill/preview",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        AssertPaymentInvalid(await ReadJsonAsync(preview));
        var confirm = await op.PostAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        AssertPaymentInvalid(await ReadJsonAsync(confirm));
        Assert.Equal(before, await WalletSpendableAsync(op, customerId));
    }

    [Fact]
    public async Task gate_mutant_b1_session_confirm_step_down_wallet_49000_ok_wallet_50000_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var sessionId = await SessionBillOpenAsync(api, op, customerId, "ps5-b1", customAmountToman: 49_000L);
        await PutRoundingAsync(manager, RoundingBody(stepToman: 1_000L, direction: "Down"));
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 49_000L } } })).EnsureSuccessStatusCode();
        using (var scope = factory.Services.CreateScope())
        {
            Assert.Equal(49_000, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().Select(i => i.CollectibleToman).SingleAsync());
        }

        await S3TestHelpers.ResetAsync(factory);
        manager = await factory.CreateOperatorClientAsync("manager", "1234");
        op = await factory.CreateOperatorClientAsync();
        await PutRoundingAsync(manager, RoundingBody(stepToman: 5_000L, direction: "Up"));
        var customerId2 = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId2}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var sessionId2 = await SessionBillOpenAsync(api, op, customerId2, "ps5-b1b");
        await PutRoundingAsync(manager, RoundingBody(stepToman: 1_000L, direction: "Down"));
        var bad = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId2}/bill/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 50_000L } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        AssertPaymentInvalid(await ReadJsonAsync(bad));
    }

    [Fact]
    public void gate_resolver_live_payment_replaces_stale_rate_card_rounding()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = db.BranchRounding.AsNoTracking().Single();
        var downRow = new BranchRoundingEntity
        {
            RoundingEnabled = true,
            StepToman = 1_000,
            Direction = "Down",
            ApplyToWallet = row.ApplyToWallet,
            WalletMinBillToman = row.WalletMinBillToman,
            AllowWalletShortBalanceDebt = row.AllowWalletShortBalanceDebt,
        };
        var snapshot = new BranchRatesSnapshot(
            PilotSeed.CreateCard(),
            RoundingPolicy.Create(5_000, RoundingDirection.Up));
        var resolved = RoundingSettingsResolver.ResolveRatesForPayment(snapshot, downRow);
        Assert.Equal(RoundingPolicy.Create(1_000, RoundingDirection.Down), resolved.Rounding);
    }
}