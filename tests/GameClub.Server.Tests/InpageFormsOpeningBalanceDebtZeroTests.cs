using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsOpeningBalanceDebtZeroTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task opening_balance_accepts_real_10000_gift_0_debt_0_without_zero_ledger_line()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var created = await mgr.PostAsJsonAsync("/api/operator/customers", new { name = "OB0", mobile = "09127770001" });
        created.EnsureSuccessStatusCode();
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var res = await mgr.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/opening-balance",
            new { realToman = 10_000L, giftToman = 0L, debtToman = 0L });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var member = await db.CustomerMembers.SingleAsync(m => m.CustomerId == customerId);
        Assert.Equal(10_000L, member.RealToman);
        Assert.Equal(0L, member.GiftToman);

        var ledger = await db.WalletLedger.Where(e => e.CustomerId == customerId).ToListAsync();
        Assert.Single(ledger);
        Assert.Equal(10_000L, ledger[0].RealDeltaToman);
        Assert.Equal(0L, ledger[0].GiftDeltaToman);
        Assert.DoesNotContain(ledger, e => e is { RealDeltaToman: 0, GiftDeltaToman: 0 });
        Assert.Equal(WalletLedgerKind.OpeningBalance, ledger[0].Kind);
    }
}