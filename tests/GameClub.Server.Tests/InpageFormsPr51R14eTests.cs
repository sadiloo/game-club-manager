using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsPr51R14eTests(GameClubWebApplicationFactory factory)
{
    private static async Task PutRoundingAsync(HttpClient mgr, object body)
        => (await mgr.PutAsJsonAsync("/api/operator/settings/rounding", body)).EnsureSuccessStatusCode();

    private static object RoundingUp5k()
        => new
        {
            enabled = true,
            stepToman = 5_000L,
            direction = "Up",
            applyToWallet = true,
            walletMinBillToman = 5_000L,
            allowWalletShortBalanceDebt = true,
        };

    private static object RoundingDown1k()
        => new
        {
            enabled = true,
            stepToman = 1_000L,
            direction = "Down",
            applyToWallet = true,
            walletMinBillToman = 5_000L,
            allowWalletShortBalanceDebt = true,
        };

    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r14e", mobile = $"0912{Random.Shared.Next(10000000, 99999999)}" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<long> WalletSpendableAsync(HttpClient op, Guid customerId)
        => (await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet")).GetProperty("spendableToman").GetInt64();

    private static async Task<long> WalletRealAsync(HttpClient op, Guid customerId)
        => (await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet")).GetProperty("realToman").GetInt64();

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static void AssertPaymentInvalid400(JsonElement body, long expectedCollectibleToman = 50_000)
    {
        Assert.Equal("payment_invalid", body.GetProperty("code").GetString());
        var latin = expectedCollectibleToman.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
            .Replace(",", "٬", StringComparison.Ordinal);
        var expectedFragment = GameClub.Server.Operator.Ss14BillDisplay.ToPersianDigits(latin);
        Assert.Contains(expectedFragment, body.GetProperty("message").GetString() ?? "");
    }

    private async Task<Guid> OpenSession49500BillAsync(HttpClient api, HttpClient op, Guid customerId, string station)
    {
        await S3TestHelpers.RegisterConsole(api, op, station, 1);
        factory.Clock.SetTehran(2026, 6, 10, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, station, 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        factory.Clock.SetTehran(2026, 6, 10, 14, 0);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        return sessionId;
    }

  /// <summary>T10: Down→Up rounding change, no preview token; W49,000 must 400 like fresh preview — no ledger/debt.</summary>
    [Fact]
    public async Task r14e_t10_wallet49000_confirm_after_down_to_up_rounding_no_preview_token_rejected_nothing_written()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await PutRoundingAsync(mgr, RoundingDown1k());
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var beforeSpendable = await WalletSpendableAsync(op, customerId);
        var realBefore = await WalletRealAsync(op, customerId);
        var sessionId = await OpenSession49500BillAsync(api, op, customerId, "PS5-T10");
        await PutRoundingAsync(mgr, RoundingUp5k());

        var preview = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 49_000L } } });
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        AssertPaymentInvalid400(await ReadJsonAsync(preview));

        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 49_000L } } });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        AssertPaymentInvalid400(await ReadJsonAsync(confirm));

        Assert.Equal(beforeSpendable, await WalletSpendableAsync(op, customerId));
        Assert.Equal(realBefore, await WalletRealAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Invoices.CountAsync());
        Assert.Equal(0, await db.PaymentLines.CountAsync());
        Assert.Equal(0, await db.WalletLedger.CountAsync(w => w.Kind == WalletLedgerKind.InvoiceSpend));
    }

    /// <summary>Mutant pin: silent gap-debt settlement must not replace explicit 400 for T10.</summary>
    [Fact]
    public async Task gate_mutant_r14e_t10_debt_floor_gap_settlement_dies()
    {
        await r14e_t10_wallet49000_confirm_after_down_to_up_rounding_no_preview_token_rejected_nothing_written();
    }

    [Fact]
    public async Task r14e_a_wallet49500_cash1000_settles_lines_change_500_preview_matches_confirm()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var before = await WalletSpendableAsync(op, customerId);
        var sessionId = await OpenSession49500BillAsync(api, op, customerId, "PS5-R14E-A");
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 49_500L },
            new { method = "Cash", amountToman = 1_000L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var bill = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(50_000, bill!.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(500, bill.GetProperty("changeToman").GetInt64());
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = 50_000L, payments });
        confirm.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.PaymentLines).SingleAsync();
        Assert.Equal(50_000, invoice.CollectibleToman);
        Assert.Equal(49_500, invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Wallet).AmountToman);
        Assert.Equal(500, invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Cash).AmountToman);
        Assert.Equal(before - 49_500, await WalletSpendableAsync(op, customerId));
    }
}