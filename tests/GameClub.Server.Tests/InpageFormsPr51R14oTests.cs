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
        var invoice = await db.Invoices.Include(i => i.PaymentLines).SingleAsync(i => i.SessionId == sessionId);
        Assert.Equal(15_500, invoice.CollectibleToman);
        Assert.NotEqual(20_000, invoice.CollectibleToman);
        Assert.Equal(500, invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Wallet).AmountToman);
        Assert.Equal(15_000, invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Cash).AmountToman);
        var spend = await db.WalletLedger.AsNoTracking().SingleAsync(w => w.Kind == WalletLedgerKind.InvoiceSpend);
        Assert.Equal(-500, spend.RealDeltaToman + spend.GiftDeltaToman);
        var member = await db.CustomerMembers.SingleAsync(m => m.CustomerId == invoice.CustomerId);
        Assert.Equal(0, member.RealToman);
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
        var previewBody = (await preview.Content.ReadFromJsonAsync<JsonElement>())!;
        var confirmBody = (await confirm.Content.ReadFromJsonAsync<JsonElement>())!;
        AssertPaymentInvalidPayableZero(previewBody);
        AssertPaymentInvalidPayableZero(confirmBody);
        Assert.Equal(previewBody.GetProperty("message").GetString(), confirmBody.GetProperty("message").GetString());
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Invoices.CountAsync(i => i.SessionId == sessionId));
    }

    [Fact]
    public async Task r14o_zero_bill_guest_wallet_preview_confirm_400_wallet_requires_member()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-R14O-ZERO-G", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 11, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-R14O-ZERO-G");
        factory.Clock.SetTehran(2026, 6, 11, 14, 0, 40);
        (await api.PostAsync($"/api/sessions/{sessionId}/stop", null)).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = await SnapshotGuestBillingStateAsync(db, sessionId);
        var payments = new[] { new { method = "Wallet", amountToman = 1_000L } };
        var previewPath = $"/api/operator/sessions/{sessionId}/bill/preview";
        var confirmPath = $"/api/operator/sessions/{sessionId}/bill/confirm";
        var preview = await op.PostAsJsonAsync(previewPath, new { payments });
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        var previewBody = (await preview.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("wallet_requires_member", previewBody.GetProperty("code").GetString());
        var confirm = await op.PostAsJsonAsync(confirmPath, new { previewCollectibleToman = 0L, payments });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        var confirmBody = (await confirm.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("wallet_requires_member", confirmBody.GetProperty("code").GetString());
        var after = await SnapshotGuestBillingStateAsync(db, sessionId);
        Assert.Equal(before, after);
    }

    private static async Task<GuestBillingSnapshot> SnapshotGuestBillingStateAsync(AppDbContext db, Guid sessionId)
    {
        var invoiceIds = await db.Invoices.AsNoTracking().Where(i => i.SessionId == sessionId).Select(i => i.Id).ToListAsync();
        return new GuestBillingSnapshot(
            invoiceIds.Count,
            await db.PaymentLines.AsNoTracking().CountAsync(p => invoiceIds.Contains(p.InvoiceId)),
            await db.WalletLedger.AsNoTracking().CountAsync(w => w.InvoiceId != null && invoiceIds.Contains(w.InvoiceId!.Value)),
            await db.Sessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.Status).SingleAsync(),
            await db.Sessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.CustomerId).SingleAsync());
    }

    private readonly record struct GuestBillingSnapshot(
        int InvoiceCount,
        int PaymentLineCount,
        int WalletLedgerCount,
        SessionStatus SessionStatus,
        Guid? CustomerId);

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
        var previewBody = (await preview.Content.ReadFromJsonAsync<JsonElement>())!;
        var confirmBody = (await confirm.Content.ReadFromJsonAsync<JsonElement>())!;
        AssertPaymentInvalidPayableZero(previewBody);
        AssertPaymentInvalidPayableZero(confirmBody);
        Assert.Equal(previewBody.GetProperty("message").GetString(), confirmBody.GetProperty("message").GetString());
    }

    [Fact]
    public async Task gate_mutant_r14o_zero_bill_wallet_preview_bypass_kills_parity_pin()
    {
        await r14o_zero_bill_wallet_only_preview_confirm_same_400_payable_zero();
    }

    private static void AssertPaymentInvalidPayableZero(JsonElement body)
    {
        Assert.Equal("payment_invalid", body.GetProperty("code").GetString());
        Assert.Equal("مبلغ قابل پرداخت ۰ تومان است", body.GetProperty("message").GetString());
    }
}