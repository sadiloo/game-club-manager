using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class WalletLoyaltyAcceptanceTests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _api = factory.CreateClientWithKey();

    private async Task<HttpClient> OpAsync(string role = "operator")
        => role == "manager"
            ? await factory.CreateOperatorClientAsync("manager", "1234")
            : await factory.CreateOperatorClientAsync();

    private static async Task<JsonElement> CreateCustomerAsync(HttpClient op, string mobile, Guid? linkSessionId = null)
    {
        var response = await op.PostAsJsonAsync("/api/operator/customers", new
        {
            name = "عضو",
            mobile,
            linkSessionId,
        });
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task wl_scenario_01_cash_top_up_immediate_and_c2c_pending_until_confirm()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await OpAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-W1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-W1", 1);
        var customer = await CreateCustomerAsync(op, "09121111111", sessionId);
        var customerId = customer.GetProperty("id").GetGuid();

        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L });
        var afterCash = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(50_000, afterCash.GetProperty("spendableToman").GetInt64());

        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "CardToCard", amountToman = 30_000L, destinationCardId = "card-a", reference = "99" });
        var afterPending = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(50_000, afterPending.GetProperty("spendableToman").GetInt64());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pendingId = await db.PendingWalletTopUps.Where(p => p.CustomerId == customerId).Select(p => p.Id).SingleAsync();
            await op.PostAsync($"/api/operator/members/wallet/pending/{pendingId}/confirm", null);
        }

        var afterConfirm = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(80_000, afterConfirm.GetProperty("spendableToman").GetInt64());
    }

    [Fact]
    public async Task wl_scenario_02_proportional_real_gift_withdrawal_matches_billing()
    {
        var pocket = new WalletPocket(80_000, 20_000);
        Assert.True(pocket.TrySpend(10_000, DebtPolicy.None, out var updated, out var gift, out var real));
        Assert.Equal(8_000, real);
        Assert.Equal(2_000, gift);
        Assert.Equal(90_000, updated.TotalToman);
    }

    [Fact]
    public async Task wl_scenario_09_level_below_first_threshold_is_zero_percent()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await OpAsync("manager");
        var ladder = LevelLadder.Default;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
            var settings = await db.BranchMemberSettings.SingleAsync();
            settings.LevelLadderJson = MemberSettingsMapper.SerializeLadder(ladder);
            await db.SaveChangesAsync();
        }

        Assert.Null(ladder.Resolve(49));
        Assert.Equal("Bronze", ladder.Resolve(50)!.Name);
        Assert.Equal(5, ladder.Resolve(50)!.DiscountPercent);
    }

    [Fact]
    public async Task wl_scenario_08_linked_member_wallet_pays_console_bill()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await OpAsync("manager");
        await mgr.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        var customer = await CreateCustomerAsync(mgr, "09123333333");
        var customerId = customer.GetProperty("id").GetGuid();
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new { method = "Cash", amountToman = 200_000L });
        var op = await OpAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-B8", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-B8", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        var collect = bill.GetProperty("collectibleToman").GetInt64();
        var response = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new
        {
            payments = new[] { new { method = "Wallet", amountToman = collect } },
        });
        response.EnsureSuccessStatusCode();
        var wallet = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        Assert.Equal(200_000 - collect, wallet.GetProperty("spendableToman").GetInt64());
    }

    [Fact]
    public async Task wl_scenario_14_package_minutes_decrease_after_play()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await OpAsync("manager");
        var customer = await CreateCustomerAsync(mgr, "09124444444");
        var customerId = customer.GetProperty("id").GetGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customerId });
            db.PackageProducts.Add(new PackageProductEntity { Id = "pkg120", Name = "۲ ساعت", PriceToman = 220_000, Minutes = 120, IsActive = true });
            db.CustomerPackages.Add(new CustomerPackageEntity
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                Name = "۲ ساعت",
                RemainingMinutes = 120,
                FaceValueToman = 220_000,
            });
            await db.SaveChangesAsync();
        }

        await S3TestHelpers.RegisterConsole(_api, mgr, "PS5-PKG", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-PKG", 1);
        await mgr.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        await mgr.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        var bill = await mgr.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        var collectible = bill.GetProperty("collectibleToman").GetInt64();
        await mgr.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new
        {
            payments = new[] { new { method = "Cash", amountToman = collectible } },
        });
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var remain = await db.CustomerPackages.Where(p => p.CustomerId == customerId).Select(p => p.RemainingMinutes).SingleAsync();
            Assert.True(remain < 120);
        }
    }

    [Fact]
    public async Task wl_scenario_15_opening_balance_labeled_and_manager_only_for_gift()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await OpAsync("manager");
        var customer = await CreateCustomerAsync(op, "09122222222");
        var customerId = customer.GetProperty("id").GetGuid();
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/opening-balance", new
        {
            realToman = 10_000L,
            giftToman = 5_000L,
            lifetimeHours = 50L,
        });
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var label = await db.WalletLedger.Where(e => e.CustomerId == customerId && e.Kind == WalletLedgerKind.OpeningBalance)
                .Select(e => e.Label).SingleAsync();
            Assert.Equal("مانده‌ی افتتاحیه", label);
            var hours = await db.CustomerMembers.Where(m => m.CustomerId == customerId).Select(m => m.LifetimePlayMinutes).SingleAsync();
            Assert.Equal(50 * 60, hours);
        }

        var opOnly = await OpAsync();
        var denied = await opOnly.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/gift", new { giftToman = 1L });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
}
