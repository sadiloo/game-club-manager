using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

internal static class PaymentSettlementParityAssert
{
    internal sealed record ParityOutcome(
        HttpStatusCode Status,
        string? Code,
        string Message,
        long? CollectibleToman,
        long? ChangeToman,
        long? WalletDebitToman);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static ParityOutcome ParseOutcome(HttpStatusCode status, JsonElement body, bool buffetShape)
    {
        if (status == HttpStatusCode.BadRequest)
        {
            return new ParityOutcome(
                status,
                body.GetProperty("code").GetString(),
                body.GetProperty("message").GetString()!,
                null,
                null,
                null);
        }

        if (buffetShape)
        {
            var bill = body.TryGetProperty("bill", out var nested) ? nested : body;
            return new ParityOutcome(
                status,
                null,
                string.Empty,
                bill.GetProperty("collectibleToman").GetInt64(),
                bill.TryGetProperty("changeToman", out var ch) ? ch.GetInt64() : null,
                bill.TryGetProperty("walletDebitToman", out var w) ? w.GetInt64() : null);
        }

        return new ParityOutcome(
            status,
            null,
            string.Empty,
            body.GetProperty("collectibleToman").GetInt64(),
            body.TryGetProperty("changeToman", out var change) ? change.GetInt64() : null,
            body.TryGetProperty("walletDebitToman", out var wallet) ? wallet.GetInt64() : null);
    }

    private static void AssertParity(ParityOutcome preview, ParityOutcome confirm)
    {
        Assert.Equal(preview.Status, confirm.Status);
        if (preview.Status == HttpStatusCode.BadRequest)
        {
            Assert.Equal(preview.Code, confirm.Code);
            Assert.Equal(preview.Message, confirm.Message);
            return;
        }

        Assert.Equal(preview.CollectibleToman, confirm.CollectibleToman);
        Assert.Equal(preview.ChangeToman, confirm.ChangeToman);
        Assert.Equal(preview.WalletDebitToman, confirm.WalletDebitToman);
    }

    internal static async Task AssertEndpointsParityAsync(
        HttpClient op,
        string previewPath,
        string confirmPath,
        object previewBody,
        object? confirmBody,
        bool buffetShape,
        GameClubWebApplicationFactory? factory = null)
    {
        var preview = await op.PostAsJsonAsync(previewPath, previewBody);
        var previewJson = await ReadJsonAsync(preview);
        var confirm = await op.PostAsJsonAsync(confirmPath, confirmBody ?? previewBody);
        var confirmJson = await ReadJsonAsync(confirm);
        var previewOutcome = ParseOutcome(preview.StatusCode, previewJson, buffetShape);
        var confirmOutcome = ParseOutcome(confirm.StatusCode, confirmJson, buffetShape);
        AssertParity(previewOutcome, confirmOutcome);

        if (factory is null || previewOutcome.Status != HttpStatusCode.OK)
        {
            return;
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.PaymentLines).OrderByDescending(i => i.Number).FirstAsync();
        Assert.Equal(previewOutcome.CollectibleToman, invoice.CollectibleToman);
        if (previewOutcome.WalletDebitToman is long walletDebit)
        {
            var walletLine = invoice.PaymentLines.SingleOrDefault(p => p.Method == PaymentMethod.Wallet);
            if (walletLine is not null)
            {
                Assert.Equal(walletDebit, walletLine.AmountToman);
            }
        }

        var recordedSum = invoice.PaymentLines.Sum(p => p.AmountToman);
        Assert.Equal(previewOutcome.CollectibleToman, recordedSum);
    }
}