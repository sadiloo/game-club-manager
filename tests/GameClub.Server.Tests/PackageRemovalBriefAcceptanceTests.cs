using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class PackageRemovalBriefAcceptanceTests(GameClubWebApplicationFactory factory)
{
    static async Task<Guid> CreateCustomerAsync(HttpClient op, string mobile)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "c", mobile });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetGuid();
    }

    static async Task SetGiftCapAsync(HttpClient mgr, long maxToman, int maxPercent)
    {
        (await mgr.PutAsJsonAsync("/api/operator/members/settings/operator-gift-cap",
            new { maxToman, maxPercent })).EnsureSuccessStatusCode();
    }

    static async Task<JsonElement> ShiftPreviewAsync(HttpClient op)
    {
        var resp = await op.GetAsync("/api/operator/shift/close/preview");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    static void AssertCloseMoney(JsonElement preview, long cashExpected, long cashReceived, long giftIssued)
    {
        Assert.Equal(cashExpected, preview.GetProperty("expected").GetProperty("cashExpected").GetInt64());
        Assert.Equal(cashReceived, preview.GetProperty("received").GetProperty("cashReceived").GetInt64());
        Assert.Equal(giftIssued, preview.GetProperty("giftIssuedToman").GetInt64());
    }

    [Fact]
    public async Task example_1_shift_received_expected_and_gift_issued()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await SetGiftCapAsync(mgr, 50_000, 10);
        var customerId = await CreateCustomerAsync(op, "09128100001");
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 300_000L, giftToman = 30_000L })).EnsureSuccessStatusCode();
        var preview = await ShiftPreviewAsync(op);
        AssertCloseMoney(preview, 300_000, 300_000, 30_000);
    }

    [Fact]
    public async Task example_2_gift_cap_body_includes_max_allowed_30000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await SetGiftCapAsync(mgr, 50_000, 10);
        var customerId = await CreateCustomerAsync(op, "09128100002");
        var resp = await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 300_000L, giftToman = 31_000L });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(30_000, body.GetProperty("maxAllowedGiftToman").GetInt64());
    }

    [Fact]
    public async Task example_3_fixed_cap_rejects_operator_manager_accepts_60000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await SetGiftCapAsync(mgr, 50_000, 10);
        var customerId = await CreateCustomerAsync(op, "09128100003");
        var bad = await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 1_000_000L, giftToman = 60_000L });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        (await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 1_000_000L, giftToman = 60_000L })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task example_4_default_zero_cap_blocks_operator_gift()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op, "09128100004");
        var resp = await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 10_000L, giftToman = 1L });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 10_000L })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task example_5a_real_topup_clears_debt_gift_stacks()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await SetGiftCapAsync(mgr, 500_000, 100);
        var customerId = await CreateCustomerAsync(op, "09128100051");
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/opening-balance",
            new { debtToman = 40_000L });
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100_000L, giftToman = 10_000L })).EnsureSuccessStatusCode();
        var wallet = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(60_000, wallet.GetProperty("realToman").GetInt64());
        Assert.Equal(10_000, wallet.GetProperty("giftToman").GetInt64());
        Assert.Equal(70_000, wallet.GetProperty("spendableToman").GetInt64());
    }

    [Fact]
    public async Task example_5b_gift_does_not_clear_debt()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await SetGiftCapAsync(mgr, 500_000, 100);
        var customerId = await CreateCustomerAsync(op, "09128100005");
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/opening-balance",
            new { debtToman = 40_000L });
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 30_000L, giftToman = 3_000L });
        var wallet = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(-10_000, wallet.GetProperty("realToman").GetInt64());
        Assert.Equal(3_000, wallet.GetProperty("giftToman").GetInt64());
    }

    [Fact]
    public async Task example_6_c2c_gift_only_on_confirm()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await SetGiftCapAsync(mgr, 50_000, 10);
        var customerId = await CreateCustomerAsync(op, "09128100006");
        var topUp = await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "CardToCard", amountToman = 200_000L, giftToman = 20_000L, destinationCardId = "card-a", reference = "c2c6" });
        topUp.EnsureSuccessStatusCode();
        long pendingId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            pendingId = await db.PendingWalletTopUps.AsNoTracking().Where(p => p.CustomerId == customerId)
                .Select(p => p.Id).SingleAsync();
        }
        var previewPending = await ShiftPreviewAsync(op);
        Assert.Equal(0, previewPending.GetProperty("giftIssuedToman").GetInt64());
        await op.PostAsync($"/api/operator/members/wallet/pending/{pendingId}/confirm", null);
        var preview = await ShiftPreviewAsync(op);
        Assert.Equal(200_000, preview.GetProperty("received").GetProperty("cardToCardConfirmedReceived").GetInt64());
        Assert.Equal(20_000, preview.GetProperty("giftIssuedToman").GetInt64());
        var customerId2 = await CreateCustomerAsync(op, "09128100066");
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId2}/wallet/top-up",
            new { method = "CardToCard", amountToman = 50_000L, giftToman = 5_000L, destinationCardId = "card-a", reference = "rej" });
        long pendingId2;
        using (var scope2 = factory.Services.CreateScope())
        {
            var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
            pendingId2 = await db.PendingWalletTopUps.AsNoTracking().Where(p => p.CustomerId == customerId2)
                .Select(p => p.Id).SingleAsync();
        }
        await op.PostAsync($"/api/operator/members/wallet/pending/{pendingId2}/reject", null);
        var preview2 = await ShiftPreviewAsync(op);
        Assert.Equal(20_000, preview2.GetProperty("giftIssuedToman").GetInt64());
    }

    [Fact]
    public async Task example_11_proportional_wallet_spend_leaves_35000_spendable()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await SetGiftCapAsync(mgr, 500_000, 100);
        var customerId = await CreateCustomerAsync(op, "09128100011");
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, giftToman = 10_000L })).EnsureSuccessStatusCode();
        await WalletSpendHelper.SpendAsync(op, customerId, 35_000);
        var wallet = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(30_000, wallet.GetProperty("realToman").GetInt64());
        Assert.Equal(5_000, wallet.GetProperty("giftToman").GetInt64());
        Assert.Equal(35_000, wallet.GetProperty("spendableToman").GetInt64());
        var preview = await ShiftPreviewAsync(op);
        Assert.Equal(60_000, preview.GetProperty("received").GetProperty("cashReceived").GetInt64());
    }

    [Fact]
    public async Task operator_gift_cap_percent_zero_blocks_any_gift_toman_cap_only()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await SetGiftCapAsync(mgr, 50_000, 0);
        var customerId = await CreateCustomerAsync(op, "09128100071");
        var resp = await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 300_000L, giftToman = 1L });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("maxAllowedGiftToman").GetInt64());
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 300_000L })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task operator_gift_cap_percent_hundred_cap_only_by_amount()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await SetGiftCapAsync(mgr, 10_000_000, 100);
        var customerId = await CreateCustomerAsync(op, "09128100072");
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 40_000L, giftToman = 40_000L })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
                new { method = "Cash", amountToman = 40_000L, giftToman = 40_001L })).StatusCode);
    }

    [Fact]
    public async Task gift_cap_equal_accepted_one_toman_over_rejected()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await SetGiftCapAsync(mgr, 30_000, 10);
        var customerId = await CreateCustomerAsync(op, "09128100070");
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 300_000L, giftToman = 30_000L })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
                new { method = "Cash", amountToman = 300_000L, giftToman = 30_001L })).StatusCode);
    }

    [Fact]
    public async Task gift_cap_audit_on_manager_set()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        await SetGiftCapAsync(mgr, 50_000, 10);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Contains(await db.AuditLog.AsNoTracking().Where(a => a.Action == "settings.operator_gift_cap").ToListAsync(),
            _ => true);
    }

    [Fact]
    public async Task counter_service_only_invoice_counted_once_in_services_toman()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("openSaleId").GetGuid();
        await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CatalogSeed.CustomServiceId, customAmountToman = 25_000L });
        await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null);
        await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Cash", amountToman = 25_000L } } });
        await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 25_000L,
            actualCardReaderToman = 0L,
            sessionActions = Array.Empty<object>(),
        });
        var income = await mgr.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=6&day=3");
        Assert.Equal(25_000, income.GetProperty("servicesToman").GetInt64());
        Assert.Equal(0, income.GetProperty("playToman").GetInt64());
    }

    [Fact]
    public async Task gift_issued_report_includes_topup_manager_gift_and_correction_not_opening()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await SetGiftCapAsync(mgr, 50_000, 10);
        var customerId = await CreateCustomerAsync(op, "09128100080");
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/opening-balance",
            new { giftToman = 9_999L });
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100_000L, giftToman = 10_000L });
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/gift", new { giftToman = 5_000L });
        var topUps = await op.GetFromJsonAsync<JsonElement[]>($"/api/operator/members/customers/{customerId}/wallet/top-ups");
        var groupId = topUps![0].GetProperty("topUpGroupId").GetGuid();
        await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new { newAmountToman = 100_000L, newGiftToman = 8_000L, description = "هدیه کمتر", commandId = Guid.NewGuid() });
        var close = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 100_000L,
            actualCardReaderToman = 0L,
            sessionActions = Array.Empty<object>(),
        });
        close.EnsureSuccessStatusCode();
        var income = await mgr.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=6&day=3");
        Assert.Equal(13_000, income.GetProperty("giftIssuedToman").GetInt64());
        Assert.Equal(0, income.GetProperty("postCloseCorrections").GetProperty("gift").GetInt64());
    }
}

internal static class WalletSpendHelper
{
    public static async Task SpendAsync(HttpClient op, Guid customerId, long amount)
    {
        var bo = (await op.PostAsync("/api/operator/buffet-only/start", null)).EnsureSuccessStatusCode();
        var openSaleId = (await bo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CatalogSeed.CustomServiceId, customAmountToman = amount });
        await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null);
        await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = amount } } });
    }
}