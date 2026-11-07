using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class ShiftReportsFixRound4Tests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _api = factory.CreateClientWithKey();

    private static string AppJsPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameClub.Server", "wwwroot", "operator", "app.js"));

    private async Task<HttpClient> ManagerAsync() => await factory.CreateOperatorClientAsync("manager", "1234");

    private async Task<HttpClient> OpAsync() => await factory.CreateOperatorClientAsync();

    private static async Task BuffetOnlyCashAsync(HttpClient op, long amountToman)
    {
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        start.EnsureSuccessStatusCode();
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("openSaleId").GetGuid();
        await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/lines", new
        {
            catalogItemId = "custom-service",
            customAmountToman = amountToman,
        });
        await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null);
        (await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm", new
        {
            payments = new[] { new { method = "Cash", amountToman = amountToman } },
        })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task sr_r4_off_same_working_day_two_closes_keep_separate_recomputed_expected()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, false);
        factory.Clock.SetTehran(2026, 11, 20, 14, 0);
        var customer = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "same-day", mobile = "09128880001" }))
            .EnsureSuccessStatusCode();
        var customerId = (await customer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new { method = "Cash", amountToman = 20_000L }))
            .EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 11, 20, 14, 5);
        var close1 = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 11, day = 20 },
            actualCashToman = 20_000L,
            actualCardReaderToman = 0L,
        });
        close1.EnsureSuccessStatusCode();
        var close1Id = (await close1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();

        factory.Clock.SetTehran(2026, 11, 20, 16, 0);
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new { method = "Cash", amountToman = 30_000L }))
            .EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 11, 20, 16, 5);
        var close2 = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 11, day = 20 },
            actualCashToman = 30_000L,
            actualCardReaderToman = 0L,
        });
        close2.EnsureSuccessStatusCode();
        var close2Id = (await close2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();

        var inc1 = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{close1Id}/income");
        var inc2 = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{close2Id}/income");
        Assert.Equal(20_000, inc1.GetProperty("expectedCashToman").GetInt64());
        Assert.Equal(30_000, inc2.GetProperty("expectedCashToman").GetInt64());
        var daily = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=11&day=20");
        Assert.Equal(50_000, daily.GetProperty("receivedMoneyToman").GetInt64());
    }

    [Fact]
    public async Task sr_r4_off_same_day_late_c2c_then_cash_stays_on_originating_close()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await manager.PutAsJsonAsync("/api/operator/destination-cards/qa-card-a", new { label = "A" });
        await S3TestHelpers.EnableShiftsAsync(manager, false);
        factory.Clock.SetTehran(2026, 12, 4, 14, 0);
        var customer = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "qa-sameday", mobile = "09128880002" }))
            .EnsureSuccessStatusCode();
        var customerId = (await customer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new
        {
            method = "CardToCard",
            amountToman = 30_000L,
            destinationCardId = "qa-card-a",
        })).EnsureSuccessStatusCode();

        long pendingId;
        using (var scope = factory.Services.CreateScope())
        {
            pendingId = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .PendingWalletTopUps.Select(p => p.Id).SingleAsync();
        }

        factory.Clock.SetTehran(2026, 12, 4, 14, 5);
        var close1 = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 12, day = 4 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
        });
        close1.EnsureSuccessStatusCode();
        var close1Id = (await close1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();

        factory.Clock.SetTehran(2026, 12, 4, 15, 0);
        await op.PostAsync($"/api/operator/members/wallet/pending/{pendingId}/confirm", null);

        var customer2 = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "cash2", mobile = "09128880003" }))
            .EnsureSuccessStatusCode();
        var customer2Id = (await customer2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Clock.SetTehran(2026, 12, 4, 16, 0);
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customer2Id}/wallet/top-up", new { method = "Cash", amountToman = 20_000L }))
            .EnsureSuccessStatusCode();

        factory.Clock.SetTehran(2026, 12, 4, 16, 5);
        var close2 = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 12, day = 4 },
            actualCashToman = 20_000L,
            actualCardReaderToman = 0L,
        });
        close2.EnsureSuccessStatusCode();
        var close2Id = (await close2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();

        var inc1 = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{close1Id}/income");
        var inc2 = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{close2Id}/income");
        Assert.Equal(0, inc1.GetProperty("expectedCashToman").GetInt64());
        Assert.Equal(30_000, inc1.GetProperty("expectedCardToCardToman").GetInt64());
        Assert.Equal(20_000, inc2.GetProperty("expectedCashToman").GetInt64());
        Assert.Equal(0, inc2.GetProperty("expectedCardToCardToman").GetInt64());
    }

    [Fact]
    public async Task sr_r4_monthly_and_yearly_roll_up_station_operator_destination_breakdowns()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await manager.PutAsJsonAsync("/api/operator/destination-cards/qa-card-a", new { label = "A" });
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-R4-M", 1);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        factory.Clock.SetTehran(2026, 11, 7, 14, 0);
        var customer = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "rollup", mobile = "09128880004" }))
            .EnsureSuccessStatusCode();
        var customerId = (await customer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new
        {
            method = "CardToCard",
            amountToman = 80_000L,
            destinationCardId = "qa-card-a",
        })).EnsureSuccessStatusCode();
        long pendingId;
        using (var scope = factory.Services.CreateScope())
        {
            pendingId = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .PendingWalletTopUps.Select(p => p.Id).SingleAsync();
        }

        await op.PostAsync($"/api/operator/members/wallet/pending/{pendingId}/confirm", null);
        await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 11, day = 7 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
        });

        var jDay = JalaliDate.FromGregorian(new LocalDate(2026, 11, 7));
        var monthly = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/income/monthly?year={jDay.Year}&month={jDay.Month}");
        Assert.True(monthly.GetProperty("byOperator").GetArrayLength() > 0);
        Assert.True(monthly.GetProperty("destinationCards").GetArrayLength() > 0);
        Assert.Equal(80_000, monthly.GetProperty("destinationCards")[0].GetProperty("totalToman").GetInt64());

        var yearly = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/income/yearly?year={jDay.Year}");
        Assert.True(yearly.GetProperty("byOperator").GetArrayLength() > 0);
        Assert.True(yearly.GetProperty("destinationCards").GetArrayLength() > 0);
    }

    [Fact]
    public async Task sr_r4_history_pwa_maps_gregorian_working_day_to_solar_hijri_display()
    {
        var js = await File.ReadAllTextAsync(AppJsPath());
        Assert.Contains("gregorianToJalali", js);
        Assert.Contains("/api/operator/shift/gregorian/", js);

        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, false);
        factory.Clock.SetTehran(2026, 11, 11, 12, 0);
        await BuffetOnlyCashAsync(op, 40_000);
        await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 11, day = 11 },
            actualCashToman = 40_000L,
            actualCardReaderToman = 0L,
        });

        var history = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/history");
        var row = history.EnumerateArray().Single();
        var wd = row.GetProperty("workingDay");
        Assert.Equal(2026, wd.GetProperty("workingDayYear").GetInt32());
        Assert.Equal(11, wd.GetProperty("workingDayMonth").GetInt32());
        Assert.Equal(11, wd.GetProperty("workingDayDay").GetInt32());

        var jalali = JalaliDate.FromGregorian(new LocalDate(2026, 11, 11));
        Assert.Equal(1405, jalali.Year);
        Assert.Equal(8, jalali.Month);
        Assert.Equal(20, jalali.Day);

        var conv = await manager.GetFromJsonAsync<JsonElement>("/api/operator/shift/gregorian/2026/11/11");
        Assert.Equal(jalali.Year, conv.GetProperty("year").GetInt32());
        Assert.Equal(jalali.Month, conv.GetProperty("month").GetInt32());
        Assert.Equal(jalali.Day, conv.GetProperty("day").GetInt32());
    }
}
