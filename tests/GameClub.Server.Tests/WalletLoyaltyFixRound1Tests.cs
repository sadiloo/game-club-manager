using System.Net;
using System.Net.Http.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class WalletLoyaltyFixRound1Tests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _api = factory.CreateClientWithKey();

    private async Task<HttpClient> MgrAsync() => await factory.CreateOperatorClientAsync("manager", "1234");

    private async Task<HttpClient> OpAsync() => await factory.CreateOperatorClientAsync();

    private static async Task<Guid> CreateCustomerAsync(HttpClient op, string mobile)
    {
        var response = await op.PostAsJsonAsync("/api/operator/customers", new { name = "عضو", mobile });
        return (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task round1_a1_cash_then_gift_spend_splits_real_and_gift()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await OpAsync();
        var mgr = await MgrAsync();
        var customerId = await CreateCustomerAsync(op, "09121110001");
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 80_000L });
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/gift",
            new { giftToman = 20_000L });
        var bo = (await op.PostAsync("/api/operator/buffet-only/start", null)).EnsureSuccessStatusCode();
        var openSaleId = (await bo.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/lines", new { catalogItemId = "tea" });
        await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null);
        var confirm = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 10_000L } } });
        confirm.EnsureSuccessStatusCode();
        var wallet = await op.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(72_000, wallet.GetProperty("realToman").GetInt64());
        Assert.Equal(18_000, wallet.GetProperty("giftToman").GetInt64());
    }

    [Fact]
    public async Task round1_a5_linked_member_without_wallet_row_can_pay_buffet_wallet()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await MgrAsync();
        var op = await OpAsync();
        await mgr.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        var customerId = await CreateCustomerAsync(op, "09121110002");
        var bo = (await op.PostAsync("/api/operator/buffet-only/start", null)).EnsureSuccessStatusCode();
        var openSaleId = (await bo.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/lines", new { catalogItemId = "chips" });
        await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null);
        var confirm = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 20_000L } } });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
    }

    [Fact]
    public async Task round1_a6_opening_debt_appears_in_ledger()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await MgrAsync();
        var customerId = await CreateCustomerAsync(mgr, "09121110003");
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/opening-balance",
            new { debtToman = 10_000L, giftToman = 5_000L, lifetimeHours = 49L });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ledger = await db.WalletLedger.SingleAsync(e => e.CustomerId == customerId && e.Kind == WalletLedgerKind.OpeningBalance);
        Assert.Equal(-10_000, ledger.RealDeltaToman);
        Assert.Equal(5_000, ledger.GiftDeltaToman);
        var customer2 = await CreateCustomerAsync(mgr, "09121110033");
        var negGift = await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customer2}/opening-balance",
            new { giftToman = -1L });
        Assert.Equal(HttpStatusCode.BadRequest, negGift.StatusCode);
    }

    [Fact]
    public async Task round1_b1_bill_preview_matches_planned_wallet_payable()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await OpAsync();
        var customerId = await CreateCustomerAsync(op, "09121110004");
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100_000L });
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-R1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-R1", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 91_667L } } });
        preview.EnsureSuccessStatusCode();
        var body = await preview.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(91_667, body.GetProperty("collectibleToman").GetInt64());
    }

    [Fact]
    public async Task round1_a3_package_covers_time_with_zero_cash_collectible()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await MgrAsync();
        var customerId = await CreateCustomerAsync(mgr, "09121110005");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customerId });
            db.CustomerPackages.Add(new CustomerPackageEntity
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                Name = "pkg",
                RemainingMinutes = 120,
                FaceValueToman = 1,
            });
            await db.SaveChangesAsync();
        }

        await S3TestHelpers.RegisterConsole(_api, mgr, "PS5-PKG-R1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-PKG-R1", 1);
        await mgr.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        await mgr.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        var bill = await mgr.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal(0, bill.GetProperty("collectibleToman").GetInt64());
    }

    [Fact]
    public async Task round1_c1_birthday_routes_return_204()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await MgrAsync();
        var customerId = await CreateCustomerAsync(mgr, "09121110006");
        var settings = await mgr.PutAsJsonAsync("/api/operator/members/settings/birthday",
            new { percent = 10, windowKind = "birth_month", windowDays = 1, anchor = "on_and_after" });
        Assert.Equal(HttpStatusCode.NoContent, settings.StatusCode);
        var birthday = await mgr.PutAsJsonAsync($"/api/operator/members/customers/{customerId}/birthday",
            new { year = 1380, month = 1, day = 1 });
        Assert.Equal(HttpStatusCode.NoContent, birthday.StatusCode);
    }
}
