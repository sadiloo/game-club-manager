using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class ShiftReportsFixRound7Tests(GameClubWebApplicationFactory factory)
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
    public async Task sr_r7_mixed_off_then_on_same_amount_c2c_distinct_days()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await manager.PutAsJsonAsync("/api/operator/destination-cards/r7-card", new { label = "R7" });
        await S3TestHelpers.EnableShiftsAsync(manager, false);

        factory.Clock.SetTehran(2026, 12, 28, 14, 0);
        var customer = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "r7-mixed", mobile = "09128884001" }))
            .EnsureSuccessStatusCode();
        var customerId = (await customer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new
        {
            method = "CardToCard",
            amountToman = 30_000L,
            destinationCardId = "r7-card",
        })).EnsureSuccessStatusCode();
        var pending1 = await LastPendingIdAsync(factory);

        factory.Clock.SetTehran(2026, 12, 28, 14, 5);
        var closeA = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 12, day = 28 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
        });
        closeA.EnsureSuccessStatusCode();
        var closeAId = (await closeA.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();

        factory.Clock.SetTehran(2026, 12, 28, 15, 0);
        (await op.PostAsync($"/api/operator/members/wallet/pending/{pending1}/confirm", null)).EnsureSuccessStatusCode();

        var incAMid = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{closeAId}/income");
        Assert.Equal(30_000, incAMid.GetProperty("expectedCardToCardToman").GetInt64());

        factory.Clock.SetTehran(2026, 12, 28, 16, 0);
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);

        factory.Clock.SetTehran(2026, 12, 28, 16, 5);
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new
        {
            method = "CardToCard",
            amountToman = 30_000L,
            destinationCardId = "r7-card",
        })).EnsureSuccessStatusCode();
        var pending2 = await LastPendingIdAsync(factory);

        factory.Clock.SetTehran(2026, 12, 28, 16, 10);
        (await op.PostAsync($"/api/operator/members/wallet/pending/{pending2}/confirm", null)).EnsureSuccessStatusCode();

        factory.Clock.SetTehran(2026, 12, 28, 16, 15);
        var closeB = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 12, day = 29 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
        });
        closeB.EnsureSuccessStatusCode();
        var closeBId = (await closeB.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();

        var incA = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{closeAId}/income");
        var incB = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{closeBId}/income");
        Assert.Equal(30_000, incA.GetProperty("expectedCardToCardToman").GetInt64());
        Assert.Equal(30_000, incB.GetProperty("expectedCardToCardToman").GetInt64());
        Assert.Equal(0, incA.GetProperty("actualCashToman").GetInt64());
        Assert.Equal(0, incB.GetProperty("actualCashToman").GetInt64());

        var dailyD1 = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=12&day=28");
        var dailyD2 = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=12&day=29");
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
}
