using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class PackageRemovalPr37FixR1Tests(GameClubWebApplicationFactory factory)
{
    private static async Task<HttpResponseMessage[]> RaceAsync(params Func<Task<HttpResponseMessage>>[] actions)
    {
        using var gate = new ManualResetEventSlim(false);
        var tasks = actions.Select(a => Task.Run(async () =>
        {
            gate.Wait();
            return await a();
        })).ToArray();
        gate.Set();
        return await Task.WhenAll(tasks);
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient op, string mobile)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "c", mobile });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CashTopUpGroupAsync(HttpClient op, Guid customerId, long amount, long gift = 0, Guid? commandId = null)
    {
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            WalletTopUpTestJson.Cash(amount, gift, commandId))).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.WalletLedger.AsNoTracking()
            .Where(w => w.CustomerId == customerId && w.Kind == WalletLedgerKind.TopUpReal)
            .OrderByDescending(w => w.AtUtc)
            .Select(w => w.TopUpGroupId!.Value)
            .FirstAsync();
    }

    private static async Task SetGiftCapAsync(HttpClient mgr, long maxToman, int maxPercent)
    {
        (await mgr.PutAsJsonAsync("/api/operator/members/settings/operator-gift-cap",
            new { maxToman, maxPercent })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task fixr1_b_topup_missing_command_id_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op, "09129000001");
        var resp = await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 10_000L });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("command_id_required", err.GetProperty("code").GetString());
    }

    [Fact]
    public async Task fixr1_a_correction_concurrent_same_command_id_one_delta()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var op2 = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await SetGiftCapAsync(mgr, 500_000, 100);
        for (var round = 0; round < 20; round++)
        {
            await S3TestHelpers.OpenShiftAsync(op, 0);
            var customerId = await CreateCustomerAsync(op, $"09129{round:D5}01");
            var groupId = await CashTopUpGroupAsync(op, customerId, 500_000);
            var cmd = Guid.NewGuid();
            var body = new { newAmountToman = 400_000L, newGiftToman = 0L, description = "race", commandId = cmd };
            var responses = await RaceAsync(
                () => op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct", body),
                () => op2.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct", body));
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.WalletTopUpCorrections.CountAsync(c => c.TopUpGroupId == groupId));
            var wallet = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
            Assert.Equal(400_000, wallet.GetProperty("realToman").GetInt64());
            var preview = await op.GetFromJsonAsync<JsonElement>("/api/operator/shift/close/preview");
            Assert.Equal(400_000, preview.GetProperty("expected").GetProperty("cashExpected").GetInt64());
            Assert.Equal(400_000, preview.GetProperty("received").GetProperty("cashReceived").GetInt64());
            await op.PostAsJsonAsync("/api/operator/shift/close", new
            {
                workingDay = new { year = 2026, month = 6, day = 3 },
                actualCashToman = 400_000L,
                actualCardReaderToman = 0L,
            });
        }
    }

    [Fact]
    public async Task fixr1_a_correction_concurrent_review_50k_to_500k_not_950k()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var op2 = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await SetGiftCapAsync(mgr, 500_000, 100);
        for (var round = 0; round < 20; round++)
        {
            await S3TestHelpers.OpenShiftAsync(op, 0);
            var customerId = await CreateCustomerAsync(op, $"09129{round:D5}02");
            (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
                WalletTopUpTestJson.CardReader(50_000))).EnsureSuccessStatusCode();
            using var scopeG = factory.Services.CreateScope();
            var dbG = scopeG.ServiceProvider.GetRequiredService<AppDbContext>();
            var groupId = await dbG.WalletLedger.AsNoTracking()
                .Where(w => w.CustomerId == customerId && w.Kind == WalletLedgerKind.TopUpReal)
                .Select(w => w.TopUpGroupId!.Value)
                .FirstAsync();
            var target = new { newAmountToman = 500_000L, newGiftToman = 0L };
            var bodyA = new { target.newAmountToman, target.newGiftToman, description = "up", commandId = Guid.NewGuid() };
            var bodyB = new { target.newAmountToman, target.newGiftToman, description = "up2", commandId = Guid.NewGuid() };
            var responses = await RaceAsync(
                () => op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct", bodyA),
                () => op2.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct", bodyB));
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
            var wallet = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
            Assert.Equal(500_000, wallet.GetProperty("realToman").GetInt64());
            var preview = await op.GetFromJsonAsync<JsonElement>("/api/operator/shift/close/preview");
            Assert.Equal(500_000, preview.GetProperty("expected").GetProperty("cardReaderExpected").GetInt64());
            await op.PostAsJsonAsync("/api/operator/shift/close", new
            {
                workingDay = new { year = 2026, month = 6, day = 3 },
                actualCashToman = 0L,
                actualCardReaderToman = 500_000L,
            });
        }
    }

    [Fact]
    public async Task fixr1_b_topup_duplicate_command_id_sequential_and_concurrent_one_credit()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var op2 = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await SetGiftCapAsync(mgr, 500_000, 100);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        for (var round = 0; round < 20; round++)
        {
            var customerId = await CreateCustomerAsync(op, $"09129{round:D5}10");
            var cmd = Guid.NewGuid();
            var body = WalletTopUpTestJson.Cash(100_000, 10_000, cmd);
            (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", body)).EnsureSuccessStatusCode();
            (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", body)).EnsureSuccessStatusCode();
            var race = await RaceAsync(
                () => op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", body),
                () => op2.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", body));
            Assert.All(race, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.WalletLedger.CountAsync(w => w.CustomerId == customerId && w.Kind == WalletLedgerKind.TopUpReal));
            var wallet = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
            Assert.Equal(100_000, wallet.GetProperty("realToman").GetInt64());
            Assert.Equal(10_000, wallet.GetProperty("giftToman").GetInt64());
        }
    }

    [Fact]
    public async Task fixr1_b_topup_duplicate_command_id_different_body_409()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customerId = await CreateCustomerAsync(op, "09129000011");
        var cmd = Guid.NewGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            WalletTopUpTestJson.Cash(100_000, 0, cmd))).EnsureSuccessStatusCode();
        var dup = await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            WalletTopUpTestJson.Cash(200_000, 0, cmd));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("command_id_reused", (await dup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task fixr1_c_example_5d_boundary_40000_accept_39999_reject()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await SetGiftCapAsync(mgr, 500_000, 100);
        var customerId = await CreateCustomerAsync(op, "09129000020");
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/opening-balance", new { debtToman = 40_000L });
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            WalletTopUpTestJson.Cash(40_000, 4_000))).EnsureSuccessStatusCode();
        var customer2 = await CreateCustomerAsync(op, "09129000021");
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customer2}/opening-balance", new { debtToman = 40_000L });
        var bad = await op.PostAsJsonAsync($"/api/operator/members/customers/{customer2}/wallet/top-up",
            WalletTopUpTestJson.Cash(39_999, 1_000));
        Assert.Equal(HttpStatusCode.Conflict, bad.StatusCode);
    }

    [Fact]
    public async Task fixr1_d_manager_gift_between_shifts_on_last_close_working_day_daily_only()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customerId = await CreateCustomerAsync(op, "09129000030");
        await CashTopUpGroupAsync(op, customerId, 50_000);
        await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 5 },
            actualCashToman = 50_000L,
            actualCardReaderToman = 0L,
        });
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/gift", new { giftToman = 5_000L });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gift = await db.WalletLedger.AsNoTracking().OrderByDescending(g => g.Id).FirstAsync();
        Assert.Null(gift.ShiftId);
        Assert.Equal(2026, gift.WorkingDayYear);
        Assert.Equal(6, gift.WorkingDayMonth);
        Assert.Equal(5, gift.WorkingDayDay);
        var daily = await mgr.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=6&day=5");
        Assert.Equal(5_000, daily.GetProperty("giftIssuedToman").GetInt64());
    }

    [Fact]
    public async Task fixr1_a_correction_concurrent_same_target_different_command_ids_second_no_change()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var op2 = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        for (var round = 0; round < 20; round++)
        {
            await S3TestHelpers.OpenShiftAsync(op, 0);
            var customerId = await CreateCustomerAsync(op, $"09129{round:D5}03");
            var groupId = await CashTopUpGroupAsync(op, customerId, 100_000);
            var a = new { newAmountToman = 80_000L, newGiftToman = 0L, description = "a", commandId = Guid.NewGuid() };
            var b = new { newAmountToman = 80_000L, newGiftToman = 0L, description = "b", commandId = Guid.NewGuid() };
            var responses = await RaceAsync(
                () => op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct", a),
                () => op2.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct", b));
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
            await op.PostAsJsonAsync("/api/operator/shift/close", new
            {
                workingDay = new { year = 2026, month = 6, day = 3 },
                actualCashToman = 80_000L,
                actualCardReaderToman = 0L,
            });
        }
    }

    [Fact]
    public async Task fixr1_c_example_5c_debt_cleared_with_gift()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await SetGiftCapAsync(mgr, 500_000, 100);
        var customerId = await CreateCustomerAsync(op, "09129000022");
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/opening-balance", new { debtToman = 40_000L });
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            WalletTopUpTestJson.Cash(50_000, 5_000))).EnsureSuccessStatusCode();
        var wallet = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(10_000, wallet.GetProperty("realToman").GetInt64());
        Assert.Equal(5_000, wallet.GetProperty("giftToman").GetInt64());
    }

    [Fact]
    public async Task fixr1_c_example_5g_manager_gift_exempt_from_debt_gate()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op, "09129000023");
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/opening-balance", new { debtToman = 10_000L });
        (await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/gift", new { giftToman = 5_000L }))
            .EnsureSuccessStatusCode();
        var wallet = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(5_000, wallet.GetProperty("giftToman").GetInt64());
    }

    [Fact]
    public async Task fixr1_e_operator_confirms_manager_c2c_over_operator_cap_credits_gift()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await SetGiftCapAsync(mgr, 20_000, 10);
        await mgr.PutAsJsonAsync("/api/operator/destination-cards/qa-a", new { label = "A" });
        var customerId = await CreateCustomerAsync(op, "09129000040");
        var reg = await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            WalletTopUpTestJson.CardToCard(200_000, 20_000, "qa-a", "e-test"));
        reg.EnsureSuccessStatusCode();
        var pending = await op.GetFromJsonAsync<JsonElement[]>("/api/operator/members/wallet/pending");
        var pendingId = pending![0].GetProperty("id").GetInt64();
        await op.PostAsync($"/api/operator/members/wallet/pending/{pendingId}/confirm", null);
        var wallet = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(200_000, wallet.GetProperty("realToman").GetInt64());
        Assert.Equal(20_000, wallet.GetProperty("giftToman").GetInt64());
    }
}