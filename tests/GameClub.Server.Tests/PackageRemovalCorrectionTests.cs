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
public sealed class PackageRemovalCorrectionTests(GameClubWebApplicationFactory factory)
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

    async Task<Guid> CashTopUpAsync(HttpClient client, Guid customerId, long amount, long gift = 0)
    {
        var resp = await client.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = amount, giftToman = gift });
        resp.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.WalletLedger.AsNoTracking()
            .Where(w => w.CustomerId == customerId && w.Kind == WalletLedgerKind.TopUpReal)
            .OrderByDescending(w => w.AtUtc)
            .Select(w => w.TopUpGroupId!.Value)
            .FirstAsync();
    }

    [Fact]
    public async Task correction_requires_description()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customerId = await CreateCustomerAsync(op, "09128200001");
        var groupId = await CashTopUpAsync(op, customerId, 50_000);
        var resp = await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new { newAmountToman = 40_000L, newGiftToman = 0L, description = "   ", commandId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task example_7_correct_downward_cash_shift_totals()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customerId = await CreateCustomerAsync(op, "09128200007");
        var groupId = await CashTopUpAsync(op, customerId, 500_000);
        await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new
            {
                newAmountToman = 50_000L,
                newGiftToman = 0L,
                description = "۵۰۰ اشتباه تایپ شد، ۵۰ گرفته شد",
                commandId = Guid.NewGuid(),
            });
        var preview = await op.GetFromJsonAsync<JsonElement>("/api/operator/shift/close/preview");
        Assert.Equal(50_000, preview.GetProperty("expected").GetProperty("cashExpected").GetInt64());
        Assert.Equal(50_000, preview.GetProperty("received").GetProperty("cashReceived").GetInt64());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.WalletTopUpCorrections.AsNoTracking().SingleAsync();
        Assert.Equal(500_000, row.BeforeAmountToman);
        Assert.Equal(50_000, row.AfterAmountToman);
    }

    [Fact]
    public async Task example_8_correct_upward_card_reader()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customerId = await CreateCustomerAsync(op, "09128200008");
        var resp = await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "CardReader", amountToman = 50_000L });
        resp.EnsureSuccessStatusCode();
        var groupId = (await op.GetFromJsonAsync<JsonElement[]>($"/api/operator/members/customers/{customerId}/wallet/top-ups"))![0]
            .GetProperty("topUpGroupId").GetGuid();
        await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new { newAmountToman = 500_000L, newGiftToman = 0L, description = "۵۰۰۰۰۰ کشیده شد", commandId = Guid.NewGuid() });
        var preview = await op.GetFromJsonAsync<JsonElement>("/api/operator/shift/close/preview");
        Assert.Equal(500_000, preview.GetProperty("received").GetProperty("cardReaderReceived").GetInt64());
    }

    [Fact]
    public async Task example_10_correction_exceeds_balance_all_roles_and_boundary_zero()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customerId = await CreateCustomerAsync(op, "09128200010");
        var groupId = await CashTopUpAsync(op, customerId, 500_000);
        await WalletSpendAsync(factory, op, customerId, 120_000);
        var owner = await factory.CreateOperatorClientAsync("owner", "5678");
        foreach (var client in new[] { op, mgr, owner })
        {
            var resp = await client.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
                new { newAmountToman = 50_000L, newGiftToman = 0L, description = "fix", commandId = Guid.NewGuid() });
            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
            var err = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("correction_exceeds_balance", err.GetProperty("code").GetString());
        }

        await S3TestHelpers.ResetAsync(factory);
        mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        customerId = await CreateCustomerAsync(op, "09128200011");
        groupId = await CashTopUpAsync(op, customerId, 500_000);
        await WalletSpendAsync(factory, op, customerId, 50_000);
        (await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new { newAmountToman = 50_000L, newGiftToman = 0L, description = "مرز", commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();

        await S3TestHelpers.ResetAsync(factory);
        mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        customerId = await CreateCustomerAsync(op, "09128200012");
        groupId = await CashTopUpAsync(op, customerId, 500_000);
        await WalletSpendAsync(factory, op, customerId, 50_001);
        Assert.Equal(HttpStatusCode.Conflict,
            (await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
                new { newAmountToman = 50_000L, newGiftToman = 0L, description = "reject", commandId = Guid.NewGuid() })).StatusCode);
    }

    static async Task WalletSpendAsync(GameClubWebApplicationFactory factory, HttpClient op, Guid customerId, long amount)
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

    [Fact]
    public async Task example_9_operator_correction_gift_cap_reapplied_on_new_amount()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await SetGiftCapAsync(mgr, 50_000, 10);
        var customerId = await CreateCustomerAsync(op, "09128200009");
        var groupId = await CashTopUpAsync(op, customerId, 300_000, 30_000);
        var bad = await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new { newAmountToman = 200_000L, newGiftToman = 30_000L, description = "cap", commandId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        (await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new { newAmountToman = 200_000L, newGiftToman = 20_000L, description = "ok", commandId = Guid.NewGuid() }))
            .EnsureSuccessStatusCode();
        var preview = await op.GetFromJsonAsync<JsonElement>("/api/operator/shift/close/preview");
        Assert.Equal(200_000, preview.GetProperty("expected").GetProperty("cashExpected").GetInt64());
        Assert.Equal(20_000, preview.GetProperty("giftIssuedToman").GetInt64());
    }

    [Fact]
    public async Task example_13_manager_post_close_adjustment_in_received_not_expected()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customerId = await CreateCustomerAsync(op, "09128200013");
        var resp = await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "CardReader", amountToman = 50_000L });
        resp.EnsureSuccessStatusCode();
        var groupId = (await op.GetFromJsonAsync<JsonElement[]>($"/api/operator/members/customers/{customerId}/wallet/top-ups"))![0]
            .GetProperty("topUpGroupId").GetGuid();
        (await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 0L,
            actualCardReaderToman = 50_000L,
            sessionActions = Array.Empty<object>(),
        })).EnsureSuccessStatusCode();
        mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var blocked = await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new { newAmountToman = 500_000L, newGiftToman = 0L, description = "نمی‌توانم", commandId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        (await mgr.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new { newAmountToman = 500_000L, newGiftToman = 0L, description = "مدیر اصلاح", commandId = Guid.NewGuid() }))
            .EnsureSuccessStatusCode();
        var preview = await op.GetFromJsonAsync<JsonElement>("/api/operator/shift/close/preview");
        Assert.Equal(0, preview.GetProperty("expected").GetProperty("cardReaderExpected").GetInt64());
        Assert.Equal(450_000, preview.GetProperty("postCloseCorrections").GetProperty("card").GetInt64());
        Assert.Equal(450_000, preview.GetProperty("received").GetProperty("cardReaderReceived").GetInt64());
    }

    [Fact]
    public async Task operator_corrections_list_forbidden_manager_ok()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/operator/members/wallet/corrections")).StatusCode);
        (await mgr.GetAsync("/api/operator/members/wallet/corrections")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task duplicate_command_id_has_no_second_effect()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customerId = await CreateCustomerAsync(op, "09128200020");
        var groupId = await CashTopUpAsync(op, customerId, 100_000);
        var cmd = Guid.NewGuid();
        var body = new { newAmountToman = 90_000L, newGiftToman = 0L, description = "dup", commandId = cmd };
        (await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct", body)).EnsureSuccessStatusCode();
        (await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct", body)).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.WalletTopUpCorrections.CountAsync());
    }

    [Fact]
    public async Task pending_card_to_card_top_up_is_not_correctable()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await SetGiftCapAsync(mgr, 50_000, 10);
        var customerId = await CreateCustomerAsync(op, "09128200030");
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "CardToCard", amountToman = 200_000L, giftToman = 20_000L, destinationCardId = "card-a", reference = "p7" }))
            .EnsureSuccessStatusCode();
        var listed = await op.GetFromJsonAsync<JsonElement[]>($"/api/operator/members/customers/{customerId}/wallet/top-ups");
        Assert.Empty(listed!);
        var fakeGroup = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound,
            (await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{fakeGroup}/correct",
                new { newAmountToman = 100_000L, newGiftToman = 0L, description = "pending", commandId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task correction_reducing_gift_below_spent_returns_gift_already_spent()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await SetGiftCapAsync(mgr, 500_000, 100);
        var customerId = await CreateCustomerAsync(op, "09128200031");
        var groupId = await CashTopUpAsync(op, customerId, 60_000, 10_000);
        await WalletSpendAsync(factory, op, customerId, 35_000);
        var resp = await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new { newAmountToman = 60_000L, newGiftToman = 4_000L, description = "هدیه خرج شده", commandId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gift_already_spent", body.GetProperty("code").GetString());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.WalletTopUpCorrections.CountAsync());
    }

    [Fact]
    public async Task sequential_corrections_manager_list_and_audit_each_before_after()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customerId = await CreateCustomerAsync(op, "09128200032");
        await SetGiftCapAsync(mgr, 500_000, 100);
        var groupId = await CashTopUpAsync(op, customerId, 100_000, 10_000);
        var cmd1 = Guid.NewGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new { newAmountToman = 90_000L, newGiftToman = 10_000L, description = "اول", commandId = cmd1 })).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 3, 15, 1);
        var cmd2 = Guid.NewGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new { newAmountToman = 80_000L, newGiftToman = 5_000L, description = "دوم", commandId = cmd2 })).EnsureSuccessStatusCode();
        var list = await mgr.GetFromJsonAsync<JsonElement[]>("/api/operator/members/wallet/corrections");
        Assert.Equal(2, list!.Length);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.WalletTopUpCorrections.AsNoTracking().OrderByDescending(c => c.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(90_000, rows[0].BeforeAmountToman);
        Assert.Equal(80_000, rows[0].AfterAmountToman);
        Assert.Equal(10_000, rows[0].BeforeGiftToman);
        Assert.Equal(5_000, rows[0].AfterGiftToman);
        Assert.Equal(100_000, rows[1].BeforeAmountToman);
        Assert.Equal(90_000, rows[1].AfterAmountToman);
        Assert.Equal(10_000, rows[1].BeforeGiftToman);
        Assert.Equal(10_000, rows[1].AfterGiftToman);
        var firstApi = list.Single(r => r.GetProperty("description").GetString() == "اول");
        var secondApi = list.Single(r => r.GetProperty("description").GetString() == "دوم");
        Assert.Equal(100_000, firstApi.GetProperty("beforeAmountToman").GetInt64());
        Assert.Equal(90_000, secondApi.GetProperty("beforeAmountToman").GetInt64());
        var audits = await db.AuditLog.AsNoTracking().Where(a => a.Action == "wallet.topup.correction").ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.Contains(audits, a => a.DetailsJson.Contains("90") && a.DetailsJson.Contains("80"));
        Assert.Contains(audits, a => a.DetailsJson.Contains("100") && a.DetailsJson.Contains("90"));
    }

    [Fact]
    public async Task shifts_off_operator_corrects_before_day_close_manager_after_close()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, false);
        factory.Clock.SetTehran(2026, 6, 4, 14, 0);
        var customerId = await CreateCustomerAsync(op, "09128200033");
        var groupId = await CashTopUpAsync(op, customerId, 100_000);
        (await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new { newAmountToman = 90_000L, newGiftToman = 0L, description = "قبل بستن روز", commandId = Guid.NewGuid() }))
            .EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 4, 16, 0);
        var close1 = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 4 },
            actualCashToman = 90_000L,
            actualCardReaderToman = 0L,
            sessionActions = Array.Empty<object>(),
        });
        close1.EnsureSuccessStatusCode();
        var blocked = await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new { newAmountToman = 80_000L, newGiftToman = 0L, description = "بعد بستن روز", commandId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var err = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("correction_window_closed", err.GetProperty("code").GetString());
        (await mgr.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new { newAmountToman = 80_000L, newGiftToman = 0L, description = "مدیر تعدیل", commandId = Guid.NewGuid() }))
            .EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 4, 17, 0);
        var close2 = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 4 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
            sessionActions = Array.Empty<object>(),
        });
        close2.EnsureSuccessStatusCode();
        var close2Id = (await close2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();
        var inc = await mgr.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{close2Id}/income");
        var dayIncome = inc.GetProperty("income");
        Assert.Equal(-10_000, dayIncome.GetProperty("postCloseCorrections").GetProperty("cash").GetInt64());
        Assert.Equal(0, inc.GetProperty("expectedCashToman").GetInt64());
        Assert.Equal(80_000, dayIncome.GetProperty("receivedCashToman").GetInt64());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var adj = await db.WalletLedger.AsNoTracking()
            .Where(w => w.Kind == WalletLedgerKind.Correction && w.IsPostCloseAdjustment).SingleAsync(w => w.RealDeltaToman == -10_000);
        Assert.Null(adj.ShiftId);
    }
}