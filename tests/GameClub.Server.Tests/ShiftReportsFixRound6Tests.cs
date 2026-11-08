using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class ShiftReportsFixRound6Tests(GameClubWebApplicationFactory factory)
{
    private async Task<HttpClient> ManagerAsync() => await factory.CreateOperatorClientAsync("manager", "1234");

    private async Task<HttpClient> OpAsync() => await factory.CreateOperatorClientAsync();

    private static async Task<long> LastPendingIdAsync(GameClubWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PendingWalletTopUps.MaxAsync(p => p.Id);
    }

    [Fact]
    public async Task sr_r6_reverse_order_equal_c2c_distinct_selected_days()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await manager.PutAsJsonAsync("/api/operator/destination-cards/r6-card", new { label = "R6" });
        await S3TestHelpers.EnableShiftsAsync(manager, false);

        factory.Clock.SetTehran(2026, 12, 18, 14, 0);
        var customer = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "r6-reverse", mobile = "09128882001" }))
            .EnsureSuccessStatusCode();
        var customerId = (await customer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new
        {
            method = "CardToCard",
            amountToman = 30_000L,
            destinationCardId = "r6-card",
        })).EnsureSuccessStatusCode();
        var pending1 = await LastPendingIdAsync(factory);

        factory.Clock.SetTehran(2026, 12, 18, 14, 5);
        var closeA = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 12, day = 18 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
        });
        closeA.EnsureSuccessStatusCode();
        var closeAId = (await closeA.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();

        factory.Clock.SetTehran(2026, 12, 18, 16, 0);
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new
        {
            method = "CardToCard",
            amountToman = 30_000L,
            destinationCardId = "r6-card",
        })).EnsureSuccessStatusCode();
        var pending2 = await LastPendingIdAsync(factory);

        factory.Clock.SetTehran(2026, 12, 18, 16, 5);
        (await op.PostAsync($"/api/operator/members/wallet/pending/{pending2}/confirm", null)).EnsureSuccessStatusCode();

        factory.Clock.SetTehran(2026, 12, 18, 16, 10);
        var closeB = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 12, day = 19 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
        });
        closeB.EnsureSuccessStatusCode();
        var closeBId = (await closeB.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();

        var incBImmediate = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{closeBId}/income");
        Assert.Equal(30_000, incBImmediate.GetProperty("expectedCardToCardToman").GetInt64());

        factory.Clock.SetTehran(2026, 12, 18, 17, 0);
        (await op.PostAsync($"/api/operator/members/wallet/pending/{pending1}/confirm", null)).EnsureSuccessStatusCode();

        var incA = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{closeAId}/income");
        var incB = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{closeBId}/income");
        Assert.Equal(30_000, incA.GetProperty("expectedCardToCardToman").GetInt64());
        Assert.Equal(30_000, incB.GetProperty("expectedCardToCardToman").GetInt64());
        Assert.Equal(0, incA.GetProperty("actualCashToman").GetInt64());
        Assert.Equal(0, incB.GetProperty("actualCashToman").GetInt64());

        var dailyD1 = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=12&day=18");
        var dailyD2 = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=12&day=19");
        Assert.Equal(30_000, dailyD1.GetProperty("receivedMoneyToman").GetInt64());
        Assert.Equal(30_000, dailyD2.GetProperty("receivedMoneyToman").GetInt64());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(60_000, await db.CustomerMembers.Where(m => m.CustomerId == customerId).Select(m => m.RealToman).SingleAsync());
        var c2cLedgerSum = await db.WalletLedger.Where(w => w.CustomerId == customerId
            && w.Kind == WalletLedgerKind.TopUpReal
            && w.Method == PaymentMethod.CardToCard).SumAsync(w => w.RealDeltaToman);
        Assert.Equal(60_000, c2cLedgerSum);
    }

    [Fact]
    public async Task sr_r6_regression_sequential_cash_same_day_isolation()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, false);

        factory.Clock.SetTehran(2026, 11, 20, 14, 0);
        var cashCust = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "r6-cash", mobile = "09128882002" }))
            .EnsureSuccessStatusCode();
        var cashCustId = (await cashCust.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{cashCustId}/wallet/top-up", new { method = "Cash", amountToman = 20_000L }))
            .EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 11, 20, 14, 5);
        var c1 = (await (await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 11, day = 20 },
            actualCashToman = 20_000L,
            actualCardReaderToman = 0L,
        })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();
        factory.Clock.SetTehran(2026, 11, 20, 16, 0);
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{cashCustId}/wallet/top-up", new { method = "Cash", amountToman = 30_000L }))
            .EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 11, 20, 16, 5);
        var c2 = (await (await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 11, day = 20 },
            actualCashToman = 30_000L,
            actualCardReaderToman = 0L,
        })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();
        var incC1 = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{c1}/income");
        var incC2 = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{c2}/income");
        Assert.Equal(20_000, incC1.GetProperty("expectedCashToman").GetInt64());
        Assert.Equal(30_000, incC2.GetProperty("expectedCashToman").GetInt64());
    }

    [Fact]
    public async Task sr_r6_regression_single_late_c2c_then_cash_same_day()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await manager.PutAsJsonAsync("/api/operator/destination-cards/qa-card-a", new { label = "A" });
        await S3TestHelpers.EnableShiftsAsync(manager, false);

        factory.Clock.SetTehran(2026, 12, 4, 14, 0);
        var c2cCust = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "r6-late", mobile = "09128882003" }))
            .EnsureSuccessStatusCode();
        var c2cCustId = (await c2cCust.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{c2cCustId}/wallet/top-up", new
        {
            method = "CardToCard",
            amountToman = 30_000L,
            destinationCardId = "qa-card-a",
        })).EnsureSuccessStatusCode();
        var pendingLate = await LastPendingIdAsync(factory);
        factory.Clock.SetTehran(2026, 12, 4, 14, 5);
        var l1 = (await (await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 12, day = 4 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
        })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();
        factory.Clock.SetTehran(2026, 12, 4, 15, 0);
        await op.PostAsync($"/api/operator/members/wallet/pending/{pendingLate}/confirm", null);
        var cash2 = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "r6-cash2", mobile = "09128882004" }))
            .EnsureSuccessStatusCode();
        var cash2Id = (await cash2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Clock.SetTehran(2026, 12, 4, 16, 0);
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{cash2Id}/wallet/top-up", new { method = "Cash", amountToman = 20_000L }))
            .EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 12, 4, 16, 5);
        var l2 = (await (await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 12, day = 4 },
            actualCashToman = 20_000L,
            actualCardReaderToman = 0L,
        })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();
        var incL1 = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{l1}/income");
        var incL2 = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{l2}/income");
        Assert.Equal(30_000, incL1.GetProperty("expectedCardToCardToman").GetInt64());
        Assert.Equal(0, incL2.GetProperty("expectedCardToCardToman").GetInt64());
        Assert.Equal(0, incL1.GetProperty("expectedCashToman").GetInt64());
        Assert.Equal(20_000, incL2.GetProperty("expectedCashToman").GetInt64());
    }

    [Fact]
    public async Task sr_r6_regression_distinct_date_late_c2c_on_originating_close()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await manager.PutAsJsonAsync("/api/operator/destination-cards/qa-card-a", new { label = "A" });
        await S3TestHelpers.EnableShiftsAsync(manager, false);

        factory.Clock.SetTehran(2026, 11, 20, 14, 0);
        var distCust = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "r6-dist", mobile = "09128882005" }))
            .EnsureSuccessStatusCode();
        var distCustId = (await distCust.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{distCustId}/wallet/top-up", new
        {
            method = "CardToCard",
            amountToman = 30_000L,
            destinationCardId = "qa-card-a",
        })).EnsureSuccessStatusCode();
        var pendingDist = await LastPendingIdAsync(factory);
        factory.Clock.SetTehran(2026, 11, 20, 14, 5);
        var d1 = (await (await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 11, day = 20 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
        })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();
        factory.Clock.SetTehran(2026, 11, 21, 10, 0);
        await op.PostAsync($"/api/operator/members/wallet/pending/{pendingDist}/confirm", null);
        factory.Clock.SetTehran(2026, 11, 21, 10, 5);
        var d2 = (await (await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 11, day = 21 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
        })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();
        var incD1 = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{d1}/income");
        var incD2 = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{d2}/income");
        Assert.Equal(30_000, incD1.GetProperty("expectedCardToCardToman").GetInt64());
        Assert.Equal(0, incD2.GetProperty("expectedCardToCardToman").GetInt64());
    }
}
