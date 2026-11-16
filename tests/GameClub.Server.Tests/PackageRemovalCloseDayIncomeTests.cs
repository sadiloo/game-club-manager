using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class PackageRemovalCloseDayIncomeTests(GameClubWebApplicationFactory factory)
{
    static long CashSurplus(JsonElement closeIncome)
        => closeIncome.GetProperty("actualCashToman").GetInt64()
           - closeIncome.GetProperty("expectedCashToman").GetInt64();

    static long CardSurplus(JsonElement closeIncome)
        => closeIncome.GetProperty("actualCardReaderToman").GetInt64()
           - closeIncome.GetProperty("expectedCardReaderToman").GetInt64();

    static async Task<Guid> CreateCustomerAsync(HttpClient op, string mobile)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "c", mobile });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetGuid();
    }

    static async Task<Guid> CashTopUpGroupAsync(GameClubWebApplicationFactory factory, HttpClient op, Guid customerId, long amount)
    {
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = amount })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.WalletLedger.AsNoTracking()
            .Where(w => w.CustomerId == customerId && w.Kind == WalletLedgerKind.TopUpReal)
            .OrderByDescending(w => w.AtUtc)
            .Select(w => w.TopUpGroupId!.Value)
            .FirstAsync();
    }

    static async Task CardReaderTopUpGroupAsync(HttpClient op, Guid customerId, long amount)
    {
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "CardReader", amountToman = amount })).EnsureSuccessStatusCode();
    }

    static async Task<Guid> LastTopUpGroupAsync(GameClubWebApplicationFactory factory, Guid customerId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.WalletLedger.AsNoTracking()
            .Where(w => w.CustomerId == customerId && w.Kind == WalletLedgerKind.TopUpReal)
            .OrderByDescending(w => w.AtUtc)
            .Select(w => w.TopUpGroupId!.Value)
            .FirstAsync();
    }

    static async Task BuffettCardReaderAsync(HttpClient op, long amount)
    {
        var bo = (await op.PostAsync("/api/operator/buffet-only/start", null)).EnsureSuccessStatusCode();
        var openSaleId = (await bo.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CatalogSeed.CustomServiceId, customAmountToman = amount });
        await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null);
        await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "CardReader", amountToman = amount } } });
    }

    [Fact]
    public async Task example_7_close_day_income_with_shift_no_false_shortfall()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customerId = await CreateCustomerAsync(op, "09128300007");
        var groupId = await CashTopUpGroupAsync(factory, op, customerId, 500_000);
        (await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new
            {
                newAmountToman = 50_000L,
                newGiftToman = 0L,
                description = "۵۰۰ اشتباه تایپ شد، ۵۰ گرفته شد",
                commandId = Guid.NewGuid(),
            })).EnsureSuccessStatusCode();

        var close = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 7 },
            actualCashToman = 50_000L,
            actualCardReaderToman = 0L,
            sessionActions = Array.Empty<object>(),
        });
        close.EnsureSuccessStatusCode();
        var closeId = (await close.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("closeRecordId").GetGuid();

        var income = await mgr.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{closeId}/income");
        var day = income.GetProperty("income");
        Assert.Equal(50_000, income.GetProperty("expectedCashToman").GetInt64());
        Assert.Equal(50_000, income.GetProperty("actualCashToman").GetInt64());
        Assert.Equal(50_000, day.GetProperty("receivedCashToman").GetInt64());
        Assert.Equal(0, CashSurplus(income));
    }

    [Fact]
    public async Task example_7_close_day_income_shifts_off_no_false_shortfall()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, false);
        factory.Clock.SetTehran(2026, 6, 8, 14, 0);
        var customerId = await CreateCustomerAsync(op, "09128300008");
        var groupId = await CashTopUpGroupAsync(factory, op, customerId, 500_000);
        (await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new
            {
                newAmountToman = 50_000L,
                newGiftToman = 0L,
                description = "۵۰۰ اشتباه تایپ شد، ۵۰ گرفته شد",
                commandId = Guid.NewGuid(),
            })).EnsureSuccessStatusCode();

        factory.Clock.SetTehran(2026, 6, 8, 16, 0);
        var close = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 8 },
            actualCashToman = 50_000L,
            actualCardReaderToman = 0L,
            sessionActions = Array.Empty<object>(),
        });
        close.EnsureSuccessStatusCode();
        var closeId = (await close.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("closeRecordId").GetGuid();

        var income = await mgr.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{closeId}/income");
        var day = income.GetProperty("income");
        Assert.Equal(50_000, income.GetProperty("expectedCashToman").GetInt64());
        Assert.Equal(50_000, income.GetProperty("actualCashToman").GetInt64());
        Assert.Equal(50_000, day.GetProperty("receivedCashToman").GetInt64());
        Assert.Equal(0, CashSurplus(income));
        Assert.NotEqual(450_000, Math.Abs(CashSurplus(income)));
    }

    [Fact]
    public async Task example_13_close_day_income_with_shift_post_close_adjustment()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customerId = await CreateCustomerAsync(op, "09128300013");
        await CardReaderTopUpGroupAsync(op, customerId, 50_000);
        var groupId = await LastTopUpGroupAsync(factory, customerId);

        var closeA = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 13 },
            actualCashToman = 0L,
            actualCardReaderToman = 50_000L,
            sessionActions = Array.Empty<object>(),
        });
        closeA.EnsureSuccessStatusCode();
        var closeAId = (await closeA.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("closeRecordId").GetGuid();
        var incA = await mgr.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{closeAId}/income");
        Assert.Equal(50_000, incA.GetProperty("expectedCardReaderToman").GetInt64());
        Assert.Equal(50_000, incA.GetProperty("actualCardReaderToman").GetInt64());
        Assert.Equal(50_000, incA.GetProperty("income").GetProperty("receivedCardReaderToman").GetInt64());
        Assert.Equal(0, CardSurplus(incA));

        mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await BuffettCardReaderAsync(op, 200_000);
        (await mgr.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new { newAmountToman = 500_000L, newGiftToman = 0L, description = "مدیر اصلاح", commandId = Guid.NewGuid() }))
            .EnsureSuccessStatusCode();

        var closeB = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 13 },
            actualCashToman = 0L,
            actualCardReaderToman = 650_000L,
            discrepancyExplanation = "تعدیل اصلاح شارژ",
            sessionActions = Array.Empty<object>(),
        });
        closeB.EnsureSuccessStatusCode();
        var closeBId = (await closeB.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("closeRecordId").GetGuid();

        var incB = await mgr.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{closeBId}/income");
        var dayB = incB.GetProperty("income");
        Assert.Equal(200_000, incB.GetProperty("expectedCardReaderToman").GetInt64());
        Assert.Equal(650_000, incB.GetProperty("actualCardReaderToman").GetInt64());
        Assert.Equal(700_000, dayB.GetProperty("receivedCardReaderToman").GetInt64());
        Assert.Equal(450_000, dayB.GetProperty("postCloseCorrections").GetProperty("card").GetInt64());
        Assert.Equal(450_000, CardSurplus(incB));
    }

    [Fact]
    public async Task example_13_close_day_income_shifts_off_post_close_adjustment()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, false);
        factory.Clock.SetTehran(2026, 6, 14, 14, 0);
        var customerId = await CreateCustomerAsync(op, "09128300014");
        await CardReaderTopUpGroupAsync(op, customerId, 50_000);
        var groupId = await LastTopUpGroupAsync(factory, customerId);

        factory.Clock.SetTehran(2026, 6, 14, 16, 0);
        var close1 = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 14 },
            actualCashToman = 0L,
            actualCardReaderToman = 50_000L,
            sessionActions = Array.Empty<object>(),
        });
        close1.EnsureSuccessStatusCode();
        var close1Id = (await close1.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("closeRecordId").GetGuid();

        (await mgr.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new { newAmountToman = 500_000L, newGiftToman = 0L, description = "مدیر اصلاح", commandId = Guid.NewGuid() }))
            .EnsureSuccessStatusCode();

        factory.Clock.SetTehran(2026, 6, 14, 17, 0);
        var close2 = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 14 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
            sessionActions = Array.Empty<object>(),
        });
        close2.EnsureSuccessStatusCode();
        var close2Id = (await close2.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("closeRecordId").GetGuid();

        var inc1 = await mgr.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{close1Id}/income");
        Assert.Equal(50_000, inc1.GetProperty("expectedCardReaderToman").GetInt64());
        Assert.Equal(50_000, inc1.GetProperty("actualCardReaderToman").GetInt64());
        Assert.Equal(0, CardSurplus(inc1));

        var inc2 = await mgr.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{close2Id}/income");
        var day = inc2.GetProperty("income");
        Assert.Equal(0, inc2.GetProperty("expectedCardReaderToman").GetInt64());
        Assert.Equal(500_000, day.GetProperty("receivedCardReaderToman").GetInt64());
        Assert.Equal(450_000, day.GetProperty("postCloseCorrections").GetProperty("card").GetInt64());
    }

    [Fact]
    public async Task card_to_card_correction_included_in_close_day_destination_card_total()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await mgr.PutAsJsonAsync("/api/operator/destination-cards/br-corr", new { label = "BR" });
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customerId = await CreateCustomerAsync(op, "09128300020");
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new
            {
                method = "CardToCard",
                amountToman = 100_000L,
                destinationCardId = "br-corr",
                reference = "corr-dest",
            })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pendingId = await db.PendingWalletTopUps.MaxAsync(p => p.Id);
        (await op.PostAsync($"/api/operator/members/wallet/pending/{pendingId}/confirm", null)).EnsureSuccessStatusCode();
        var groupId = await LastTopUpGroupAsync(factory, customerId);
        (await op.PostAsJsonAsync($"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new { newAmountToman = 150_000L, newGiftToman = 0L, description = "کارت‌به‌کارت درست", commandId = Guid.NewGuid() }))
            .EnsureSuccessStatusCode();

        var close = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 20 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
            sessionActions = Array.Empty<object>(),
        });
        close.EnsureSuccessStatusCode();
        var closeId = (await close.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("closeRecordId").GetGuid();

        var income = await mgr.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{closeId}/income");
        var cards = income.GetProperty("income").GetProperty("destinationCards");
        var match = cards.EnumerateArray().Single(c => c.GetProperty("destinationCardId").GetString() == "br-corr");
        Assert.Equal(150_000, match.GetProperty("totalToman").GetInt64());
        Assert.Equal(150_000, income.GetProperty("expectedCardToCardToman").GetInt64());
    }
}