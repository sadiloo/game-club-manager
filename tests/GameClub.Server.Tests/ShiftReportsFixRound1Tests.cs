using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class ShiftReportsFixRound1Tests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _api = factory.CreateClientWithKey();

    private async Task<HttpClient> ManagerAsync() => await factory.CreateOperatorClientAsync("manager", "1234");

    private async Task<HttpClient> OpAsync() => await factory.CreateOperatorClientAsync();

    [Fact]
    public async Task sr_scenario_10_midnight_minutes_split_across_tehran_days()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-M", 1);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        factory.Clock.SetTehran(2026, 6, 3, 23, 30);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-M", 1);
        factory.Clock.SetTehran(2026, 6, 4, 0, 30);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        var coll = bill.GetProperty("collectibleToman").GetInt64();
        await S3TestHelpers.ConfirmCashAsync(op, sessionId, coll);
        await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = coll,
            actualCardReaderToman = 0L,
            sessionActions = Array.Empty<object>(),
        });
        async Task<long> SecondsOnDay(int day)
        {
            var from = S3TestHelpers.T(2026, 6, day, 0, 0).UtcDateTime;
            var to = S3TestHelpers.T(2026, 6, day + 1, 0, 0).UtcDateTime.AddSeconds(-1);
            var usage = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/usage?fromUtc={from:o}&toUtc={to:o}");
            var station = usage.GetProperty("byStation").EnumerateArray().FirstOrDefault(e => e.GetProperty("stationId").GetString() == "PS5-M");
            return station.ValueKind == JsonValueKind.Undefined ? 0 : station.GetProperty("durationSeconds").GetInt64();
        }

        var day3 = await SecondsOnDay(3);
        var day4 = await SecondsOnDay(4);
        Assert.InRange(day3, 1795, 1805);
        Assert.InRange(day4, 1795, 1805);
        Assert.InRange(day3 + day4, 3595, 3605);
    }

    [Fact]
    public async Task sr_scenario_11_shared_drawer_operator_breakdown_and_single_close()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await manager.PutAsJsonAsync("/api/operator/shift/settings", new { multiOperatorEnabled = true });
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await manager.PostAsJsonAsync("/api/operator/users", new { username = "op2", pin = "2222", role = "Operator" });
        var op2 = await factory.CreateOperatorClientAsync("op2", "2222");
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-O1", 1);
        await S3TestHelpers.RegisterConsole(_api, op2, "PS5-O2", 1);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var s1 = await S3TestHelpers.StartSession(_api, "PS5-O1", 1);
        var s2 = await S3TestHelpers.StartSession(_api, "PS5-O2", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{s1}/bill/open", null);
        await S3TestHelpers.ConfirmCashAsync(op, s1, 110_000);
        await op2.PostAsync($"/api/operator/sessions/{s2}/bill/open", null);
        await S3TestHelpers.ConfirmCashAsync(op2, s2, 110_000);
        await op2.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 220_000L,
            actualCardReaderToman = 0L,
            sessionActions = Array.Empty<object>(),
        });
        var daily = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=6&day=3");
        Assert.True(daily.TryGetProperty("byOperator", out var byOperator));
        Assert.Equal(2, byOperator.GetArrayLength());
        Assert.True(daily.TryGetProperty("byStation", out _));
    }

    [Fact]
    public async Task sr_off_mode_second_close_does_not_restamp_wallet_top_up()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, false);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-A1", 1);
        var customer = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "a", mobile = "09121112222" }))
            .EnsureSuccessStatusCode();
        var customerId = (await customer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var s1 = await S3TestHelpers.StartSession(_api, "PS5-A1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{s1}/bill/open", null);
        await S3TestHelpers.ConfirmCashAsync(op, s1, 110_000);
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new { method = "Cash", amountToman = 20_000L });
        await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 130_000L,
            actualCardReaderToman = 0L,
        });
        factory.Clock.SetTehran(2026, 6, 4, 14, 0);
        var s2 = await S3TestHelpers.StartSession(_api, "PS5-A1", 1);
        factory.Clock.SetTehran(2026, 6, 4, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{s2}/bill/open", null);
        await S3TestHelpers.ConfirmCashAsync(op, s2, 110_000);
        var preview = await op.GetFromJsonAsync<JsonElement>("/api/operator/shift/close/preview");
        Assert.Equal(110_000, preview.GetProperty("expected").GetProperty("cashExpected").GetInt64());
        await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 4 },
            actualCashToman = 110_000L,
            actualCardReaderToman = 0L,
        });
        var day3 = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=6&day=3");
        Assert.Equal(20_000, day3.GetProperty("walletTopUpToman").GetInt64());
        Assert.Equal(130_000, day3.GetProperty("receivedMoneyToman").GetInt64());
    }

    [Fact]
    public async Task sr_close_requires_settle_or_carry_for_open_sessions()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-A", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-B", 1);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var s1 = await S3TestHelpers.StartSession(_api, "PS5-A", 1);
        var s2 = await S3TestHelpers.StartSession(_api, "PS5-B", 1);
        var response = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
        });
        Assert.True(response.StatusCode >= HttpStatusCode.BadRequest);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{s1}/bill/open", null);
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{s1}/bill");
        var coll = bill.GetProperty("collectibleToman").GetInt64();
        (await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = coll,
            actualCardReaderToman = 0L,
            sessionActions = new[]
            {
                new { sessionId = s1, action = "settle" },
                new { sessionId = s2, action = "carry" },
            },
        })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task pr32_snapshot_has_no_pending_model_changes()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
