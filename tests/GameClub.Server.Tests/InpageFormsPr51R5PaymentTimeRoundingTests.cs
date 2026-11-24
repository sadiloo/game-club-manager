using System.Net;
using System.Net.Http.Json;
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
public sealed class InpageFormsPr51R5PaymentTimeRoundingTests(GameClubWebApplicationFactory factory)
{
    private static async Task PutRoundingAsync(HttpClient client, object body)
    {
        var res = await client.PutAsJsonAsync("/api/operator/settings/rounding", body);
        res.EnsureSuccessStatusCode();
    }

    private static object DefaultRoundingBody(
        bool? enabled = null,
        long? stepToman = null,
        string? direction = null)
        => new
        {
            enabled = enabled ?? true,
            stepToman = stepToman ?? 5_000L,
            direction = direction ?? "Up",
            applyToWallet = true,
            walletMinBillToman = 5_000L,
            allowWalletShortBalanceDebt = true,
        };

    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "ptr", mobile = $"0917{Random.Shared.Next(1000000, 9999999)}" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
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

    private async Task<Guid> SessionBillOpenAsync(HttpClient api, HttpClient op, Guid customerId, long customAmountToman, string stationId = "ps5-1")
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
    public async Task payment_time_a_buffet_rounding_off_at_open_on_at_confirm_charges_50000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        await PutRoundingAsync(manager, DefaultRoundingBody(enabled: false));
        var openSaleId = await BuffetCheckoutAsync(op, customerId, 49_500L);
        await PutRoundingAsync(manager, DefaultRoundingBody(enabled: true));
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 50_000L } } })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(50_000, invoice.CollectibleToman);
        Assert.Equal(500, invoice.RoundingToman);
    }

    [Fact]
    public async Task payment_time_a_buffet_rounding_on_at_open_off_at_confirm_charges_49500()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetCheckoutAsync(op, customerId, 49_500L);
        await PutRoundingAsync(manager, DefaultRoundingBody(enabled: false));
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 49_500L } } })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(49_500, invoice.CollectibleToman);
        Assert.Equal(0, invoice.RoundingToman);
    }

    [Fact]
    public async Task payment_time_a_session_rounding_off_at_open_on_at_confirm_charges_50000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        await PutRoundingAsync(manager, DefaultRoundingBody(enabled: false));
        var sessionId = await SessionBillOpenAsync(api, op, customerId, 49_500L);
        await PutRoundingAsync(manager, DefaultRoundingBody(enabled: true));
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 50_000L } } })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(50_000, invoice.CollectibleToman);
    }

    [Fact]
    public async Task payment_time_a_session_rounding_on_at_open_off_at_confirm_charges_49500()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var sessionId = await SessionBillOpenAsync(api, op, customerId, 49_500L);
        await PutRoundingAsync(manager, DefaultRoundingBody(enabled: false));
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 49_500L } } })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(49_500, invoice.CollectibleToman);
    }

    [Fact]
    public async Task payment_time_b_buffet_stale_preview_collectible_rejected_after_step_and_direction_change()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetCheckoutAsync(op, customerId, 49_500L);
        var stalePreview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 50_000L } } });
        stalePreview.EnsureSuccessStatusCode();
        Assert.Equal(50_000, (await stalePreview.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("bill").GetProperty("collectibleToman").GetInt64());
        await PutRoundingAsync(manager, DefaultRoundingBody(stepToman: 1_000L, direction: "Down"));
        var staleConfirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                previewCollectibleToman = 50_000L,
                payments = new[] { new { method = "Wallet", amountToman = 49_000L } },
            });
        Assert.Equal(HttpStatusCode.Conflict, staleConfirm.StatusCode);
        Assert.Equal("stale_rounding_preview", (await ReadJsonAsync(staleConfirm)).GetProperty("code").GetString());
        var freshPreview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 49_000L } } });
        freshPreview.EnsureSuccessStatusCode();
        var freshCollectible = (await freshPreview.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        Assert.Equal(49_000, freshCollectible);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                previewCollectibleToman = freshCollectible,
                payments = new[] { new { method = "Wallet", amountToman = 49_000L } },
            })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task payment_time_b_session_stale_preview_collectible_rejected_after_step_and_direction_change()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var sessionId = await SessionBillOpenAsync(api, op, customerId, 49_500L);
        var stalePreview = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 50_000L } } });
        stalePreview.EnsureSuccessStatusCode();
        Assert.Equal(50_000, (await stalePreview.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("collectibleToman").GetInt64());
        await PutRoundingAsync(manager, DefaultRoundingBody(stepToman: 1_000L, direction: "Down"));
        var staleConfirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new
            {
                previewCollectibleToman = 50_000L,
                payments = new[] { new { method = "Wallet", amountToman = 49_000L } },
            });
        Assert.Equal(HttpStatusCode.Conflict, staleConfirm.StatusCode);
        Assert.Equal("stale_rounding_preview", (await ReadJsonAsync(staleConfirm)).GetProperty("code").GetString());
        var freshPreview = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 49_000L } } });
        freshPreview.EnsureSuccessStatusCode();
        var freshCollectible = (await freshPreview.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("collectibleToman").GetInt64();
        Assert.Equal(49_000, freshCollectible);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new
            {
                previewCollectibleToman = freshCollectible,
                payments = new[] { new { method = "Wallet", amountToman = 49_000L } },
            })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task payment_time_c_unchanged_settings_buffet_49500_wallet_still_50000()
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
            new { payments = new[] { new { method = "Wallet", amountToman = 50_000L } } });
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        Assert.Equal(50_000, previewCollectible);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                previewCollectibleToman = previewCollectible,
                payments = new[] { new { method = "Wallet", amountToman = 50_000L } },
            })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(50_000, invoice.CollectibleToman);
        Assert.Equal(500, invoice.RoundingToman);
        Assert.Equal(49_500, invoice.UnroundedPayableToman);
    }

    [Fact]
    public void payment_time_resolver_session_and_open_bill_use_same_live_branch_rounding()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = db.BranchRounding.AsNoTracking().Single();
        var offRow = new BranchRoundingEntity
        {
            RoundingEnabled = false,
            StepToman = row.StepToman,
            Direction = row.Direction,
            ApplyToWallet = row.ApplyToWallet,
            WalletMinBillToman = row.WalletMinBillToman,
            AllowWalletShortBalanceDebt = row.AllowWalletShortBalanceDebt,
        };
        var snapshot = new BranchRatesSnapshot(
            PilotSeed.CreateCard(),
            RoundingPolicy.Create(5_000, RoundingDirection.Up));
        var sessionResolved = RoundingSettingsResolver.ResolveSessionRatesSnapshot(snapshot, offRow);
        var openResolved = RoundingSettingsResolver.ResolveOpenBillRatesSnapshot(snapshot, offRow);
        Assert.Equal(sessionResolved.Rounding, openResolved.Rounding);
        Assert.Equal(GameClub.Billing.RoundingPolicy.Off, sessionResolved.Rounding);
    }
}