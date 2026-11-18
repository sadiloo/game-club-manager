using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class PcInternetBillOracleTests(GameClubWebApplicationFactory factory)
{
    private static async Task EnsureOfflineRatesAsync(HttpClient mgr)
    {
        (await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new { onlineDay = 80_000L, onlineNight = 100_000L, offlineDay = 60_000L, offlineNight = 80_000L }))
            .EnsureSuccessStatusCode();
        (await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcVipGroupId}",
            new { onlineDay = 110_000L, onlineNight = 130_000L, offlineDay = 60_000L, offlineNight = 80_000L }))
            .EnsureSuccessStatusCode();
    }

    private static void AssertBill(JsonElement bill, long timeGross, params (int minutes, long amount)[] lines)
    {
        Assert.Equal(timeGross, bill.GetProperty("timeGrossToman").GetInt64());
        var segments = bill.GetProperty("segments").EnumerateArray().ToList();
        Assert.Equal(lines.Length, segments.Count);
        for (var i = 0; i < lines.Length; i++)
        {
            Assert.Equal(lines[i].minutes, segments[i].GetProperty("billedMinutes").GetInt32());
            Assert.Equal(lines[i].amount, segments[i].GetProperty("amountToman").GetInt64());
        }
    }

    private static async Task<JsonElement> BillAfterStopAsync(HttpClient op, HttpClient api, Guid sessionId)
    {
        (await api.PostAsync($"/api/sessions/{sessionId}/stop", null)).EnsureSuccessStatusCode();
        return await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
    }

    private async Task<uint> RowVersionAsync(Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
    }

    private async Task ToggleAsync(HttpClient op, Guid sessionId, bool on)
    {
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on,
            expectedVersion = await RowVersionAsync(sessionId),
            commandId = Guid.NewGuid(),
        })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task PcInternet_Oracle_E1_three_segments_110000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-O1", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 17, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-O1");
        factory.Clock.SetTehran(2026, 6, 2, 17, 45);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        await ToggleAsync(op, sessionId, false);
        factory.Clock.SetTehran(2026, 6, 2, 18, 15);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        await ToggleAsync(op, sessionId, true);
        factory.Clock.SetTehran(2026, 6, 2, 18, 30);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        var bill = await BillAfterStopAsync(op, api, sessionId);
        AssertBill(bill, 110_000, (45, 60_000), (30, 30_000), (15, 20_000));
    }

    [Fact]
    public async Task PcInternet_Oracle_E2_press_mid_minute_43667()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-O2", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 20, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-O2");
        factory.Clock.SetTehran(2026, 6, 2, 20, 10, 30);
        await ToggleAsync(op, sessionId, false);
        factory.Clock.SetTehran(2026, 6, 2, 20, 40);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        var bill = await BillAfterStopAsync(op, api, sessionId);
        AssertBill(bill, 43_667, (11, 14_667), (29, 29_000));
    }

    [Fact]
    public async Task PcInternet_Oracle_E3_night_band_103334()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-O3", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 23, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-O3");
        factory.Clock.SetTehran(2026, 6, 2, 23, 50);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        await ToggleAsync(op, sessionId, false);
        factory.Clock.SetTehran(2026, 6, 3, 0, 20);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        var bill = await BillAfterStopAsync(op, api, sessionId);
        AssertBill(bill, 103_334, (50, 66_667), (10, 10_000), (20, 26_667));
    }

    [Fact]
    public async Task PcInternet_Oracle_E4_transfer_while_off_73333()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-O4A", PilotSeed.PcNormalGroupId);
        await S3TestHelpers.RegisterPc(api, "PC-O4B", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 10, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-O4A");
        factory.Clock.SetTehran(2026, 6, 2, 10, 30);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        await ToggleAsync(op, sessionId, false);
        factory.Clock.SetTehran(2026, 6, 2, 10, 50);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        factory.Clock.SetTehran(2026, 6, 2, 10, 50);
        (await S3TestHelpers.TransferAsync(op, sessionId, "PC-O4B", expectedVersion: await RowVersionAsync(sessionId))).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 2, 11, 0);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        var bill = await BillAfterStopAsync(op, api, sessionId);
        AssertBill(bill, 73_333, (30, 40_000), (20, 20_000), (10, 13_333));
    }

    [Fact]
    public async Task PcInternet_Oracle_E6_amount_prepaid_toggle_bill_40000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-O6", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 20, 0);
        (await op.PostAsJsonAsync(
            "/api/operator/stations/PC-O6/start-guest",
            new { prepaidKind = 2, amountToman = 40_000L, openPayAtEnd = false })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var sessionId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        factory.Clock.SetTehran(2026, 6, 2, 20, 15);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        await ToggleAsync(op, sessionId, false);
        factory.Clock.SetTehran(2026, 6, 2, 20, 35);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        var bill = await BillAfterStopAsync(op, api, sessionId);
        AssertBill(bill, 40_000, (15, 20_000), (20, 20_000));
    }

    [Fact]
    public async Task PcInternet_Oracle_E6b_amount_prepaid_transfer_bill_38333()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-O6B", PilotSeed.PcNormalGroupId);
        await S3TestHelpers.RegisterPc(api, "PC-O6BV", PilotSeed.PcVipGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 20, 0);
        (await op.PostAsJsonAsync(
            "/api/operator/stations/PC-O6B/start-guest",
            new { prepaidKind = 2, amountToman = 40_000L, openPayAtEnd = false })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var sessionId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        factory.Clock.SetTehran(2026, 6, 2, 20, 15);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        factory.Clock.SetTehran(2026, 6, 2, 20, 15);
        (await S3TestHelpers.TransferAsync(op, sessionId, "PC-O6BV", expectedVersion: await RowVersionAsync(sessionId))).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 2, 20, 25);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        var bill = await BillAfterStopAsync(op, api, sessionId);
        AssertBill(bill, 38_333, (15, 20_000), (10, 18_333));
    }

    [Fact]
    public async Task PcInternet_Oracle_E7_wallet_offline_thirty_minutes_30000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-O7", PilotSeed.PcNormalGroupId);
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "E7", mobile = "09120007001" });
        created.EnsureSuccessStatusCode();
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 30_000L, commandId = Guid.NewGuid() });
        factory.Clock.SetTehran(2026, 6, 2, 20, 0);
        (await op.PostAsJsonAsync($"/api/operator/stations/PC-O7/start", new { customerId })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var sessionId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        await ToggleAsync(op, sessionId, false);
        factory.Clock.SetTehran(2026, 6, 2, 20, 30);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        var bill = await BillAfterStopAsync(op, api, sessionId);
        AssertBill(bill, 30_000, (30, 30_000));
    }

    [Fact]
    public async Task PcInternet_Oracle_E8a_reconnect_pricing_42667()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-O8A", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 20, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-O8A");
        factory.Clock.SetTehran(2026, 6, 2, 20, 5);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        factory.Clock.SetTehran(2026, 6, 2, 20, 8);
        await ToggleAsync(op, sessionId, false);
        factory.Clock.SetTehran(2026, 6, 2, 20, 10, 30);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        factory.Clock.SetTehran(2026, 6, 2, 20, 40);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        var bill = await BillAfterStopAsync(op, api, sessionId);
        AssertBill(bill, 42_667, (8, 10_667), (32, 32_000));
    }

    [Fact]
    public async Task PcInternet_Oracle_E8b_dead_pc_clamp_6667()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-O8B", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 20, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-O8B");
        factory.Clock.SetTehran(2026, 6, 2, 20, 5);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        factory.Clock.SetTehran(2026, 6, 2, 20, 8);
        await ToggleAsync(op, sessionId, false);
        factory.Clock.SetTehran(2026, 6, 2, 20, 15);
        (await api.PostAsync("/api/internal/dead-pc-sweep", null)).EnsureSuccessStatusCode();
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        AssertBill(bill, 6_667, (5, 6_667L));
    }

    [Fact]
    public async Task PcInternet_Oracle_E9_short_off_26333()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-O9", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 10, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-O9");
        factory.Clock.SetTehran(2026, 6, 2, 10, 10);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        await ToggleAsync(op, sessionId, false);
        factory.Clock.SetTehran(2026, 6, 2, 10, 10, 30);
        await ToggleAsync(op, sessionId, true);
        factory.Clock.SetTehran(2026, 6, 2, 10, 20);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        var bill = await BillAfterStopAsync(op, api, sessionId);
        AssertBill(bill, 26_333, (10, 13_333), (1, 1_000), (9, 12_000));
    }

    [Fact]
    public async Task PcInternet_Oracle_E11_duration_toggle_bill_80000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-O11", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 20, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-O11");
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/prepaid/duration", new { minutes = 60 })).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 2, 20, 15);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        await ToggleAsync(op, sessionId, false);
        factory.Clock.SetTehran(2026, 6, 2, 21, 15);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        var bill = await BillAfterStopAsync(op, api, sessionId);
        AssertBill(bill, 80_000, (15, 20_000), (60, 60_000));
    }

    [Fact]
    public async Task PcInternet_Oracle_E12_duration_midnight_bill_89333()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-O12", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 23, 30);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-O12");
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/prepaid/duration", new { minutes = 60 })).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 2, 23, 40);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        await ToggleAsync(op, sessionId, false);
        factory.Clock.SetTehran(2026, 6, 3, 0, 42);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        var bill = await BillAfterStopAsync(op, api, sessionId);
        AssertBill(bill, 89_333, (10, 13_333), (20, 20_000), (42, 56_000));
    }

    [Fact]
    public async Task PcInternet_Oracle_E13_duration_transfer_bill_78667()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-O13", PilotSeed.PcNormalGroupId);
        await S3TestHelpers.RegisterPc(api, "PC-O13V", PilotSeed.PcVipGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 20, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-O13");
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/prepaid/duration", new { minutes = 60 })).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 2, 20, 15);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        factory.Clock.SetTehran(2026, 6, 2, 20, 15);
        (await S3TestHelpers.TransferAsync(op, sessionId, "PC-O13V", expectedVersion: await RowVersionAsync(sessionId))).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 2, 20, 47);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        var bill = await BillAfterStopAsync(op, api, sessionId);
        AssertBill(bill, 78_667, (15, 20_000), (32, 58_667));
    }
}