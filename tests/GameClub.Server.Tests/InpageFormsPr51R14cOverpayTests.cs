using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsPr51R14cOverpayTests(GameClubWebApplicationFactory factory)
{
    private static async Task PutRoundingOffAsync(HttpClient manager)
    {
        (await manager.PutAsJsonAsync("/api/operator/settings/rounding", new
        {
            enabled = false,
            stepToman = 5_000L,
            direction = "Up",
            applyToWallet = true,
            walletMinBillToman = 5_000L,
            allowWalletShortBalanceDebt = true,
        })).EnsureSuccessStatusCode();
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r14c", mobile = $"0914{Random.Shared.Next(1000000, 9999999)}" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> OpenBuffet15500Async(HttpClient op, Guid customerId)
    {
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        start.EnsureSuccessStatusCode();
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 15_500L });
        (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null)).EnsureSuccessStatusCode();
        return openSaleId;
    }

    private static async Task AssertShiftExpectedCashAsync(HttpClient op, long expectedCashToman)
    {
        var body = await op.GetFromJsonAsync<JsonElement>("/api/operator/shift/close/preview");
        Assert.Equal(expectedCashToman, body.GetProperty("expected").GetProperty("cashExpected").GetInt64());
    }

    [Fact]
    public async Task r14c_overpay_cash10k_card10k_records_card10000_cash5500_change4500()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingOffAsync(manager);
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customerId = await CreateCustomerAsync(op);
        var openSaleId = await OpenBuffet15500Async(op, customerId);
        var payments = new object[]
        {
            new { method = "Cash", amountToman = 10_000L },
            new { method = "CardReader", amountToman = 10_000L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var bill = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill");
        Assert.Equal(15_500, bill.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(4_500, bill.GetProperty("changeToman").GetInt64());
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { previewCollectibleToman = 15_500L, payments })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.PaymentLines).SingleAsync();
        Assert.Equal(15_500, invoice.CollectibleToman);
        Assert.Equal(15_500, invoice.PaymentLines.Sum(p => p.AmountToman));
        Assert.Equal(10_000, invoice.PaymentLines.Single(p => p.Method == PaymentMethod.CardReader).AmountToman);
        Assert.Equal(5_500, invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Cash).AmountToman);
        Assert.Equal(4_500, 20_000 - 15_500);
        await AssertShiftExpectedCashAsync(op, 5_500);
    }

    [Fact]
    public async Task r14c_overpay_cash16k_card4k_records_card4000_cash11500()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingOffAsync(manager);
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customerId = await CreateCustomerAsync(op);
        var openSaleId = await OpenBuffet15500Async(op, customerId);
        var payments = new object[]
        {
            new { method = "Cash", amountToman = 16_000L },
            new { method = "CardReader", amountToman = 4_000L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(4_500, (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill").GetProperty("changeToman").GetInt64());
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { previewCollectibleToman = 15_500L, payments })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.PaymentLines).SingleAsync();
        Assert.Equal(4_000, invoice.PaymentLines.Single(p => p.Method == PaymentMethod.CardReader).AmountToman);
        Assert.Equal(11_500, invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Cash).AmountToman);
        await AssertShiftExpectedCashAsync(op, 11_500);
    }

    [Fact]
    public async Task r14c_card_only_20k_rejected_400_writes_nothing()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingOffAsync(manager);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        var openSaleId = await OpenBuffet15500Async(op, customerId);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "CardReader", amountToman = 20_000L } } });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Invoices.CountAsync());
        Assert.Equal(0, await db.PaymentLines.CountAsync());
        Assert.Equal(0, await db.WalletLedger.CountAsync());
    }

    [Fact]
    public async Task r14c_card15500_cash1000_no_cash_line_change1000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingOffAsync(manager);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        var openSaleId = await OpenBuffet15500Async(op, customerId);
        var payments = new object[]
        {
            new { method = "CardReader", amountToman = 15_500L },
            new { method = "Cash", amountToman = 1_000L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(1_000, (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill").GetProperty("changeToman").GetInt64());
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { previewCollectibleToman = 15_500L, payments })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lines = await db.PaymentLines.AsNoTracking().ToListAsync();
        Assert.Single(lines);
        Assert.Equal(PaymentMethod.CardReader, lines[0].Method);
        Assert.Equal(15_500, lines[0].AmountToman);
    }

    [Fact]
    public async Task gate_mutant_r14c_non_cash_over_payable_rejects_not_silent_cap()
    {
        await r14c_card_only_20k_rejected_400_writes_nothing();
    }

    [Fact]
    public async Task r14c_exact_tender_cash15500_confirms_without_zero_cash_line()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingOffAsync(manager);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        var openSaleId = await OpenBuffet15500Async(op, customerId);
        var payments = new object[] { new { method = "Cash", amountToman = 15_500L } };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(0, (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill").GetProperty("changeToman").GetInt64());
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { previewCollectibleToman = 15_500L, payments })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lines = await db.PaymentLines.AsNoTracking().ToListAsync();
        Assert.Single(lines);
        Assert.Equal(PaymentMethod.Cash, lines[0].Method);
        Assert.Equal(15_500, lines[0].AmountToman);
    }
}