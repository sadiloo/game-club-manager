using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

/// <summary>
/// R14m K8: zero-total session bill at the counter — pins current behaviour only.
/// NOTES: Box product mutant for K8 is <c>k8_mutant_cash_on_zero_bill_rejected_no_invoice_payments</c> (confirm path), not DB injection.
/// </summary>
[Collection("server")]
public sealed class InpageFormsPr51R14mK8ZeroTotalCounterPinTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task k8_zero_collectible_session_empty_payments_preview_confirm_parity()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-R14M-K8-A", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 9, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-R14M-K8-A");
        factory.Clock.SetTehran(2026, 6, 9, 12, 0, 40);
        var stop = await api.PostAsync($"/api/sessions/{sessionId}/stop", null);
        stop.EnsureSuccessStatusCode();
        var bill = await stop.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, bill.GetProperty("collectibleToman").GetInt64());

        var previewPath = $"/api/operator/sessions/{sessionId}/bill/preview";
        var confirmPath = $"/api/operator/sessions/{sessionId}/bill/confirm";
        var payments = Array.Empty<object>();
        var preview = await op.PostAsJsonAsync(previewPath, new { payments });
        preview.EnsureSuccessStatusCode();
        var previewBill = (await preview.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(0, previewBill.GetProperty("collectibleToman").GetInt64());

        var confirm = await op.PostAsJsonAsync(confirmPath, new { previewCollectibleToman = 0L, payments });
        confirm.EnsureSuccessStatusCode();
        var confirmBill = (await confirm.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(0, confirmBill.GetProperty("collectibleToman").GetInt64());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.SingleAsync(i => i.SessionId == sessionId);
        Assert.Equal(0, invoice.CollectibleToman);
        var lineCount = await db.PaymentLines.CountAsync(p => p.InvoiceId == invoice.Id);
        Assert.Equal(0, lineCount);
    }

    [Fact]
    public async Task k8_mutant_cash_on_zero_bill_rejected_no_invoice_payments()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-R14M-K8-MUT", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 9, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-R14M-K8-MUT");
        factory.Clock.SetTehran(2026, 6, 9, 14, 0, 40);
        (await api.PostAsync($"/api/sessions/{sessionId}/stop", null)).EnsureSuccessStatusCode();
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = 0L, payments = new[] { new { method = "Cash", amountToman = 1L } } });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Invoices.AnyAsync(i => i.SessionId == sessionId));
        Assert.Equal(0, await db.PaymentLines.CountAsync());
    }
}