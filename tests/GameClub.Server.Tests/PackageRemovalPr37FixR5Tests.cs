using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class PackageRemovalPr37FixR5Tests(GameClubWebApplicationFactory factory)
{
    private static async Task<Guid> CreateCustomerAsync(HttpClient op, string mobile)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "c", mobile });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetGuid();
    }

    private static async Task SetGiftCapAsync(HttpClient mgr, long maxToman, int maxPercent)
    {
        (await mgr.PutAsJsonAsync("/api/operator/members/settings/operator-gift-cap",
            new { maxToman, maxPercent })).EnsureSuccessStatusCode();
    }

    private static string CanonicalJson(JsonElement el)
        => JsonSerializer.Serialize(el, new JsonSerializerOptions { WriteIndented = false });

    [Fact]
    public async Task fixr5_cash_topup_replay_after_gift_cap_lowered_returns_original_one_credit()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await SetGiftCapAsync(mgr, 500_000, 100);
        var customerId = await CreateCustomerAsync(op, "09129010001");
        var cmd = Guid.NewGuid();
        var body = WalletTopUpTestJson.Cash(100_000, 10_000, cmd);
        var first = await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", body);
        first.EnsureSuccessStatusCode();
        var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>();

        await SetGiftCapAsync(mgr, 5_000, 5);

        var retry = await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", body);
        retry.EnsureSuccessStatusCode();
        var retryJson = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(CanonicalJson(firstJson), CanonicalJson(retryJson));

        var wallet = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(100_000, wallet.GetProperty("realToman").GetInt64());
        Assert.Equal(10_000, wallet.GetProperty("giftToman").GetInt64());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.WalletLedger.CountAsync(w =>
            w.CustomerId == customerId && w.Kind == WalletLedgerKind.TopUpReal));
        Assert.Equal(1, await db.WalletLedger.CountAsync(w =>
            w.CustomerId == customerId && w.Kind == WalletLedgerKind.TopUpGift));
        Assert.Equal(1, await db.WalletCommands.CountAsync(c => c.CommandId == cmd));
    }

    [Fact]
    public async Task fixr5_c2c_register_replay_after_gift_cap_lowered_returns_original_one_pending()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await SetGiftCapAsync(mgr, 500_000, 100);
        await mgr.PutAsJsonAsync("/api/operator/destination-cards/r5-c2c", new { label = "r5" });
        var customerId = await CreateCustomerAsync(op, "09129010002");
        var cmd = Guid.NewGuid();
        var body = WalletTopUpTestJson.CardToCard(100_000, 10_000, "r5-c2c", "r5-replay", cmd);
        var first = await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", body);
        first.EnsureSuccessStatusCode();
        var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>();

        await SetGiftCapAsync(mgr, 5_000, 5);

        var retry = await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", body);
        retry.EnsureSuccessStatusCode();
        var retryJson = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(CanonicalJson(firstJson), CanonicalJson(retryJson));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.PendingWalletTopUps.CountAsync(p =>
            p.CustomerId == customerId && p.Status == PendingWalletTopUpStatus.Pending));
        var wallet = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(0, wallet.GetProperty("realToman").GetInt64());
        Assert.Equal(0, wallet.GetProperty("giftToman").GetInt64());
    }

    [Fact]
    public async Task fixr5_new_command_id_after_gift_cap_lowered_returns_gift_cap_exceeded()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await SetGiftCapAsync(mgr, 500_000, 100);
        var customerId = await CreateCustomerAsync(op, "09129010003");
        var firstBody = WalletTopUpTestJson.Cash(100_000, 10_000, Guid.NewGuid());
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", firstBody))
            .EnsureSuccessStatusCode();

        await SetGiftCapAsync(mgr, 5_000, 5);

        var newCmd = Guid.NewGuid();
        var sameBody = WalletTopUpTestJson.Cash(100_000, 10_000, newCmd);
        var fail = await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", sameBody);
        Assert.Equal(HttpStatusCode.BadRequest, fail.StatusCode);
        Assert.Equal("gift_cap_exceeded", (await fail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var wallet = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(100_000, wallet.GetProperty("realToman").GetInt64());
        Assert.Equal(10_000, wallet.GetProperty("giftToman").GetInt64());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.WalletLedger.CountAsync(w =>
            w.CustomerId == customerId && w.Kind == WalletLedgerKind.TopUpReal));
        Assert.Equal(0, await db.WalletCommands.CountAsync(c => c.CommandId == newCmd));
    }
}