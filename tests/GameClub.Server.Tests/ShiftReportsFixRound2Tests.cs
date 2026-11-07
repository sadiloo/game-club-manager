using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class ShiftReportsFixRound2Tests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _api = factory.CreateClientWithKey();

    private async Task<HttpClient> ManagerAsync() => await factory.CreateOperatorClientAsync("manager", "1234");

    private async Task<HttpClient> OpAsync() => await factory.CreateOperatorClientAsync();

    private static async Task<HttpResponseMessage[]> RaceAsync(
        Func<Task<HttpResponseMessage>> a,
        Func<Task<HttpResponseMessage>> b)
    {
        using var gate = new ManualResetEventSlim(false);
        var ta = Task.Run(async () => { gate.Wait(); return await a(); });
        var tb = Task.Run(async () => { gate.Wait(); return await b(); });
        await Task.Delay(20);
        gate.Set();
        return await Task.WhenAll(ta, tb);
    }

    private int CloseRecordCount()
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().CloseRecords.Count();
    }

    [Fact]
    public async Task sr_r2_off_duplicate_close_one_record_and_409_loser()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await manager.PostAsJsonAsync("/api/operator/users", new { username = "op2", pin = "2222", role = "Operator" });
        var op2 = await factory.CreateOperatorClientAsync("op2", "2222");
        await S3TestHelpers.EnableShiftsAsync(manager, false);
        factory.Clock.SetTehran(2026, 6, 10, 15, 0);
        var before = CloseRecordCount();
        var responses = await RaceAsync(
            () => op.PostAsJsonAsync("/api/operator/shift/close", new
            {
                workingDay = new { year = 2026, month = 6, day = 10 },
                actualCashToman = 0L,
                actualCardReaderToman = 0L,
            }),
            () => op2.PostAsJsonAsync("/api/operator/shift/close", new
            {
                workingDay = new { year = 2026, month = 6, day = 10 },
                actualCashToman = 0L,
                actualCardReaderToman = 0L,
            }));
        Assert.Equal(1, CloseRecordCount() - before);
        Assert.Contains(responses, r => r.StatusCode is HttpStatusCode.OK);
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task sr_r2_session_confirm_vs_close_never_500()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-R2", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-R2", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        await manager.PostAsJsonAsync("/api/operator/users", new { username = "op2", pin = "2222", role = "Operator" });
        var op2 = await factory.CreateOperatorClientAsync("op2", "2222");
        var responses = await RaceAsync(
            () => op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new
            {
                payments = new[] { new { method = "Cash", amountToman = 110_000L } },
            }),
            () => op2.PostAsJsonAsync("/api/operator/shift/close", new
            {
                workingDay = new { year = 2026, month = 6, day = 3 },
                actualCashToman = 110_000L,
                actualCardReaderToman = 0L,
                discrepancyExplanation = "race",
                sessionActions = new[] { new { sessionId, action = "carry" } },
            }));
        foreach (var r in responses)
        {
            var body = await r.Content.ReadAsStringAsync();
            Assert.True((int)r.StatusCode < 500, body);
        }
    }

    [Fact]
    public async Task sr_r2_wallet_topup_then_close_appears_on_working_day()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customer = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "tu2", mobile = "09124445556" }))
            .EnsureSuccessStatusCode();
        var customerId = (await customer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new { method = "Cash", amountToman = 20_000L }))
            .EnsureSuccessStatusCode();
        (await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 20_000L,
            actualCardReaderToman = 0L,
        })).EnsureSuccessStatusCode();
        var daily = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=6&day=3");
        Assert.Equal(20_000, daily.GetProperty("walletTopUpToman").GetInt64());
    }

    [Fact]
    public async Task sr_r2_wallet_topup_racing_close_stamped_on_working_day()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await manager.PostAsJsonAsync("/api/operator/users", new { username = "op2", pin = "2222", role = "Operator" });
        var op2 = await factory.CreateOperatorClientAsync("op2", "2222");
        var customer = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "tu", mobile = "09123334444" }))
            .EnsureSuccessStatusCode();
        var customerId = (await customer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var responses = await RaceAsync(
            () => op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new { method = "Cash", amountToman = 20_000L }),
            () => op2.PostAsJsonAsync("/api/operator/shift/close", new
            {
                workingDay = new { year = 2026, month = 6, day = 3 },
                actualCashToman = 20_000L,
                actualCardReaderToman = 0L,
            }));
        foreach (var r in responses)
        {
            Assert.True((int)r.StatusCode < 500, await r.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task sr_r2_close_income_expected_per_shift_not_whole_day()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        factory.Clock.SetTehran(2026, 11, 25, 10, 0);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var c1 = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "a", mobile = "09125556666" }))
            .EnsureSuccessStatusCode();
        var id1 = (await c1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await op.PostAsJsonAsync($"/api/operator/members/customers/{id1}/wallet/top-up", new { method = "Cash", amountToman = 10_000L });
        var close1 = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 11, day = 25 },
            actualCashToman = 10_000L,
            actualCardReaderToman = 0L,
        });
        var close1Id = (await close1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();
        factory.Clock.SetTehran(2026, 11, 25, 12, 0);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var c2 = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "b", mobile = "09127778888" }))
            .EnsureSuccessStatusCode();
        var id2 = (await c2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await op.PostAsJsonAsync($"/api/operator/members/customers/{id2}/wallet/top-up", new { method = "Cash", amountToman = 20_000L });
        var close2 = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 11, day = 25 },
            actualCashToman = 20_000L,
            actualCardReaderToman = 0L,
        });
        var close2Id = (await close2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();
        var inc1 = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{close1Id}/income");
        var inc2 = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{close2Id}/income");
        Assert.Equal(10_000, inc1.GetProperty("expectedCashToman").GetInt64());
        Assert.Equal(20_000, inc2.GetProperty("expectedCashToman").GetInt64());
    }

    [Fact]
    public async Task sr_r2_destination_cards_wallet_c2c_and_void_exclusion()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await manager.PutAsJsonAsync("/api/operator/destination-cards/qa-card-a", new { label = "A" });
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-C2C", 1);
        factory.Clock.SetTehran(2026, 6, 16, 14, 0);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customer = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "wc", mobile = "09129990001" }))
            .EnsureSuccessStatusCode();
        var customerId = (await customer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new
        {
            method = "CardToCard",
            amountToman = 30_000L,
            destinationCardId = "qa-card-a",
        });
        long pendingId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            pendingId = await db.PendingWalletTopUps.Select(p => p.Id).SingleAsync();
        }

        await op.PostAsync($"/api/operator/members/wallet/pending/{pendingId}/confirm", null);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-C2C", 1);
        factory.Clock.SetTehran(2026, 6, 16, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new
        {
            payments = new[] { new { method = "CardToCard", amountToman = 110_000L, destinationCardId = "qa-card-a" } },
        });
        long invoiceNumber;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            invoiceNumber = await db.Invoices.Where(i => i.SessionId == sessionId).Select(i => i.Number).SingleAsync();
        }

        await manager.PostAsJsonAsync($"/api/operator/invoices/{invoiceNumber}/void", new { reason = "test" });
        await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 16 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
            discrepancyExplanation = "c2c",
        });
        var daily = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=6&day=16");
        var cards = daily.GetProperty("destinationCards").EnumerateArray().ToList();
        var cardA = cards.Single(c => c.GetProperty("destinationCardId").GetString() == "qa-card-a");
        Assert.Equal(30_000, cardA.GetProperty("totalToman").GetInt64());
    }

    [Fact]
    public async Task sr_r2_by_operator_wallet_topup_without_double_wallet_play()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-W", 1);
        factory.Clock.SetTehran(2026, 6, 11, 14, 0);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customer = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "wl", mobile = "09128887777" }))
            .EnsureSuccessStatusCode();
        var customerId = (await customer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new { method = "Cash", amountToman = 100_000L }))
            .EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 11, 15, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-W", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 11, 15, 30);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        var coll = bill.GetProperty("collectibleToman").GetInt64();
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new
        {
            payments = new[] { new { method = "Wallet", amountToman = coll } },
        })).EnsureSuccessStatusCode();
        (await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 11 },
            actualCashToman = 100_000L,
            actualCardReaderToman = 0L,
        })).EnsureSuccessStatusCode();
        var daily = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=6&day=11");
        Assert.Equal(100_000, daily.GetProperty("walletTopUpToman").GetInt64());
        var byOp = daily.GetProperty("byOperator").EnumerateArray().ToList();
        Assert.NotEmpty(byOp);
        Assert.Equal(100_000, byOp.Sum(e => e.GetProperty("receivedMoneyToman").GetInt64()));
    }

    [Fact]
    public async Task sr_r2_history_export_nonempty_200()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-E", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-E", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        await S3TestHelpers.ConfirmCashAsync(op, sessionId, 110_000);
        var export = await manager.GetAsync("/api/operator/reports/history/export");
        export.EnsureSuccessStatusCode();
        var csv = await export.Content.ReadAsStringAsync();
        Assert.Contains("110000", csv);
    }

    [Fact]
    public async Task sr_r2_operator_forbidden_aggregate_reports()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, false);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        var close = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
        });
        var closeId = (await close.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/operator/reports/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/operator/reports/history/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync($"/api/operator/reports/close/{closeId}/income")).StatusCode);
    }

    [Fact]
    public void sr_r2_jalali_1405_08_16_and_17_gregorian()
    {
        var d16 = JalaliCalendar.ToGregorian(1405, 8, 16);
        var d17 = JalaliCalendar.ToGregorian(1405, 8, 17);
        Assert.Equal(new LocalDate(2026, 11, 7), d16);
        Assert.Equal(new LocalDate(2026, 11, 8), d17);
    }

    [Fact]
    public async Task sr_r2_jalali_api_endpoint()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await OpAsync();
        var body = await op.GetFromJsonAsync<JsonElement>("/api/operator/shift/jalali/1405/8/16");
        Assert.Equal(2026, body.GetProperty("year").GetInt32());
        Assert.Equal(11, body.GetProperty("month").GetInt32());
        Assert.Equal(7, body.GetProperty("day").GetInt32());
    }

    [Fact]
    public void sr_r2_pwa_does_not_default_session_disposition_to_carry()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "GameClub.Server", "wwwroot", "operator", "app.js"));
        var js = File.ReadAllText(path);
        Assert.DoesNotContain("|| \"carry\"", js);
    }
}
