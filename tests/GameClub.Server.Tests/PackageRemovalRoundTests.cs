using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

/// <summary>Examples from step-package-removal-develop-brief (wallet gift on top-up).</summary>
[Collection("server")]
public sealed class PackageRemovalRoundTests(GameClubWebApplicationFactory factory) : IClassFixture<GameClubWebApplicationFactory>
{
    static async Task<Guid> CreateCustomerAsync(HttpClient op, string mobile)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "c", mobile });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task example_1_cash_topup_with_gift_under_cap()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await mgr.PutAsJsonAsync("/api/operator/members/settings/operator-gift-cap",
            new { maxToman = 50_000L, maxPercent = 10 });
        var customerId = await CreateCustomerAsync(op, "09128000001");
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 300_000L, giftToman = 30_000L });
        var wallet = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(300_000, wallet.GetProperty("realToman").GetInt64());
        Assert.Equal(30_000, wallet.GetProperty("giftToman").GetInt64());
        Assert.Equal(330_000, wallet.GetProperty("spendableToman").GetInt64());
    }

    [Fact]
    public async Task example_2_operator_gift_over_percent_cap_rejected()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await mgr.PutAsJsonAsync("/api/operator/members/settings/operator-gift-cap",
            new { maxToman = 50_000L, maxPercent = 10 });
        var customerId = await CreateCustomerAsync(op, "09128000002");
        var resp = await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 300_000L, giftToman = 31_000L });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.WalletLedger.AnyAsync(w => w.CustomerId == customerId));
    }

    [Fact]
    public async Task operator_cannot_set_gift_cap()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var put = await op.PutAsJsonAsync("/api/operator/members/settings/operator-gift-cap",
            new { maxToman = 1L, maxPercent = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }
}