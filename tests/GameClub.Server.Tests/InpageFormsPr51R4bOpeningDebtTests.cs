using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

/// <summary>PR51 R4b item 1: opening debt via debtToman (PackageRemoval path).</summary>
[Collection("server")]
public sealed class InpageFormsPr51R4bOpeningDebtTests(GameClubWebApplicationFactory factory)
{
    private static async Task<Guid> CreateCustomerAsync(HttpClient op, string mobile)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "debt", mobile });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task opening_debt_negative_real_allowed_top_up_pays_debt_before_gift()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await mgr.PutAsJsonAsync("/api/operator/members/settings/operator-gift-cap", new { maxToman = 500_000L, maxPercent = 100 });
        var customerId = await CreateCustomerAsync(op, "09128880001");
        (await mgr.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/opening-balance",
            new { debtToman = 40_000L })).EnsureSuccessStatusCode();

        var wallet = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(-40_000, wallet.GetProperty("realToman").GetInt64());
        Assert.Equal(0, wallet.GetProperty("giftToman").GetInt64());

        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100_000L, giftToman = 10_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();

        wallet = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(60_000, wallet.GetProperty("realToman").GetInt64());
        Assert.Equal(10_000, wallet.GetProperty("giftToman").GetInt64());
        Assert.Equal(70_000, wallet.GetProperty("spendableToman").GetInt64());
    }

    [Fact]
    public async Task opening_positive_real_and_gift_zero_via_same_debt_path_api()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var customerId = await CreateCustomerAsync(mgr, "09128880002");
        (await mgr.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/opening-balance",
            new { realToman = 25_000L, giftToman = 0L, debtToman = 0L })).EnsureSuccessStatusCode();
        var wallet = await mgr.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(25_000, wallet.GetProperty("realToman").GetInt64());
        Assert.Equal(0, wallet.GetProperty("giftToman").GetInt64());
    }

    [Fact]
    public async Task gift_top_up_blocked_while_opening_debt_remains_after_partial_pay()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await mgr.PutAsJsonAsync("/api/operator/members/settings/operator-gift-cap", new { maxToman = 500_000L, maxPercent = 100 });
        var customerId = await CreateCustomerAsync(op, "09128880003");
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/opening-balance", new { debtToman = 40_000L });
        var resp = await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 30_000L, giftToman = 3_000L, commandId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("gift_not_allowed_with_debt", (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.WalletLedger.CountAsync(w =>
            w.CustomerId == customerId && (w.Kind == WalletLedgerKind.TopUpReal || w.Kind == WalletLedgerKind.TopUpGift)));
    }
}