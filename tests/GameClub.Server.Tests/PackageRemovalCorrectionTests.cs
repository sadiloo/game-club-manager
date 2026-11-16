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
        foreach (var client in new[] { op, mgr })
        {
            Assert.Equal(HttpStatusCode.Conflict,
                (await client.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
                    new { newAmountToman = 50_000L, newGiftToman = 0L, description = "fix", commandId = Guid.NewGuid() })).StatusCode);
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
}