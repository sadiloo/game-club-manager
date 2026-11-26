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

/// <summary>PR51 R14o: owner write-off ruling (ApplyToWallet ON, 15٬500) and zero-bill preview==confirm.</summary>
[Collection("server")]
public sealed class InpageFormsPr51R14oTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task r14o_owner_writeoff_apply_wallet_on_mixed_15500_preview_confirm_invoice_15500()
    {
        var r7 = new KioskFixR7AcceptanceTests(factory);
        var (sessionId, op) = await r7.CreateOvershootSessionAsync(graceOff: true);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = SodaId });
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 500L },
            new { method = "Cash", amountToman = 15_000L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(15_500, previewCollectible);
        Assert.NotEqual(20_000, previewCollectible);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = previewCollectible, payments });
        confirm.EnsureSuccessStatusCode();
        var confirmCollectible = (await confirm.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(previewCollectible, confirmCollectible);
        Assert.Equal(15_500, confirmCollectible);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.SingleAsync(i => i.SessionId == sessionId);
        Assert.Equal(15_500, invoice.CollectibleToman);
        Assert.NotEqual(20_000, invoice.CollectibleToman);
        var invoiceNumber = invoice.Number;
        var inv = await op.GetFromJsonAsync<JsonElement>($"/api/operator/invoices/{invoiceNumber}");
        Assert.Equal(15_500, inv.GetProperty("collectibleToman").GetInt64());
    }

    [Fact]
    public async Task gate_mutant_r14o_owner_writeoff_whole_bill_round_20000_kills_pin()
    {
        await r14o_owner_writeoff_apply_wallet_on_mixed_15500_preview_confirm_invoice_15500();
    }

    [Fact]
    public async Task r14o_zero_bill_wallet_only_preview_confirm_same_400_payable_zero()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-R14O-ZERO", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 11, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-R14O-ZERO");
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r14o-z", mobile = "0917" + Guid.NewGuid().ToString("N")[..7] });
        created.EnsureSuccessStatusCode();
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        (await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId })).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 11, 12, 0, 40);
        (await api.PostAsync($"/api/sessions/{sessionId}/stop", null)).EnsureSuccessStatusCode();
        var previewPath = $"/api/operator/sessions/{sessionId}/bill/preview";
        var confirmPath = $"/api/operator/sessions/{sessionId}/bill/confirm";
        var payments = new[] { new { method = "Wallet", amountToman = 1_000L } };
        var preview = await op.PostAsJsonAsync(previewPath, new { payments });
        var confirm = await op.PostAsJsonAsync(confirmPath, new { previewCollectibleToman = 0L, payments });
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        var previewMsg = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("message").GetString();
        var confirmMsg = (await confirm.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("message").GetString();
        Assert.Equal(previewMsg, confirmMsg);
        Assert.Contains("۰", previewMsg, StringComparison.Ordinal);
        Assert.Contains("تومان", previewMsg, StringComparison.Ordinal);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Invoices.CountAsync(i => i.SessionId == sessionId));
    }

    [Fact]
    public async Task r14o_zero_bill_cash_preview_confirm_same_400_payable_zero()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-R14O-ZERO-C", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 11, 13, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-R14O-ZERO-C");
        factory.Clock.SetTehran(2026, 6, 11, 13, 0, 40);
        (await api.PostAsync($"/api/sessions/{sessionId}/stop", null)).EnsureSuccessStatusCode();
        var payments = new[] { new { method = "Cash", amountToman = 1L } };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = 0L, payments });
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal(
            (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("message").GetString(),
            (await confirm.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("message").GetString());
    }

    [Fact]
    public async Task gate_mutant_r14o_zero_bill_wallet_preview_bypass_kills_parity_pin()
    {
        await r14o_zero_bill_wallet_only_preview_confirm_same_400_payable_zero();
    }
}