using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Kiosk;
using GameClub.Server.Operator;
using GameClub.Server.Services;
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

    private static async Task<(SessionCharge Charge, Settlement Settlement)> MemberPlaySettlementPreviewAsync(
        GameClubWebApplicationFactory factory,
        Guid sessionId,
        Guid customerId,
        DateTime endUtc)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.AsNoTracking()
            .Include(s => s.Segments)
            .Include(s => s.Station)!.ThenInclude(st => st!.Group)
            .SingleAsync(s => s.Id == sessionId);
        var charge = await services.GetRequiredService<SessionBilling>().BuildChargeAsync(session, endUtc);
        var settings = await db.BranchMemberSettings.AsNoTracking().SingleAsync();
        var member = await db.CustomerMembers.AsNoTracking().SingleAsync(m => m.CustomerId == customerId);
        var pricing = MemberSettingsMapper.BuildPricing(
            member,
            settings,
            MemberSettingsMapper.LoadLadder(settings));
        var walletSettings = MemberSettingsMapper.LoadWalletSettings(settings);
        var money = MemberWalletMapper.ToPocket(member).TotalToman;
        var spendable = money > 0 ? money : 0;
        var inDebt = money < 0;
        var discount = settings.ApplyDiscountsToPc && charge.Lines.Count > 0 && pricing is not null
            ? new SessionDiscount(LevelPercent: pricing.LevelPercent, ValidFrom: charge.Lines[0].Start)
            : SessionDiscount.Empty;
        var settlement = AccountSettlement.Settle(
            charge,
            new PcAccount(member.CustomerId.ToString(), spendable),
            BillingPolicy.Default,
            walletSettings.GraceMinuteEnabled && !inDebt,
            discount,
            shortfallIsUncovered: inDebt);
        return (charge, settlement);
    }

    private static void AssertL12SilverTenPercentSliceDiscounts(SessionCharge charge, Settlement settlement)
    {
        var onlineGross = charge.Lines.Where(l => l.Mode == PlayMode.Online).Sum(l => l.AmountToman);
        var offlineGross = charge.Lines.Where(l => l.Mode == PlayMode.Offline).Sum(l => l.AmountToman);
        Assert.Equal(40_000, onlineGross);
        Assert.Equal(30_000, offlineGross);
        Assert.Equal(4_000, onlineGross * 10 / 100);
        Assert.Equal(3_000, offlineGross * 10 / 100);
        Assert.Equal(70_000, charge.TotalAmountToman);
        Assert.Equal(7_000, settlement.DiscountToman);
        var conserved = settlement.MoneyConsumedToman
            + settlement.DiscountToman
            + settlement.UncoveredToman
            + settlement.OmittedToman
            + settlement.WriteOffToman
            + (settlement.Grace?.WaivedToman ?? 0);
        Assert.Equal(charge.TotalAmountToman, conserved);
    }

    /// <summary>
    /// Partial-wallet L12 (11+30 min, gross 44_667): Silver level 10% is
    /// <c>MoneyMath.PercentOf(44_667, 10)</c> — one floor on the running gross (4_466.7 → 4_466),
    /// not per-slice or per-minute sums. Online slice gross 14_667 still shows slice vs
    /// per-minute discount divergence (1_466 vs 1_463).
    /// </summary>
    private static void AssertL12PartialWalletRoundDownDiscounts(SessionCharge charge, Settlement settlement)
    {
        var onlineGross = charge.Lines.Where(l => l.Mode == PlayMode.Online).Sum(l => l.AmountToman);
        var offlineGross = charge.Lines.Where(l => l.Mode == PlayMode.Offline).Sum(l => l.AmountToman);
        const int onlineMinutes = 11;
        const int offlineMinutes = 30;
        const long hourlyOnline = 80_000;
        const long hourlyOffline = 60_000;
        var remainder = BillingPolicy.Default.Remainder;
        var minuteOnlineGross = MoneyMath.ChargeForMinutes(hourlyOnline, 1, remainder);
        var minuteOfflineGross = MoneyMath.ChargeForMinutes(hourlyOffline, 1, remainder);
        Assert.Equal(onlineMinutes, charge.Lines.Where(l => l.Mode == PlayMode.Online).Sum(l => l.BilledMinutes));
        Assert.Equal(offlineMinutes, charge.Lines.Where(l => l.Mode == PlayMode.Offline).Sum(l => l.BilledMinutes));
        Assert.Equal(14_667, onlineGross);
        Assert.Equal(30_000, offlineGross);
        var onlineSliceDiscount = MoneyMath.PercentOf(onlineGross, 10);
        var offlineSliceDiscount = MoneyMath.PercentOf(offlineGross, 10);
        var perMinuteOnlineDiscount = onlineMinutes * MoneyMath.PercentOf(minuteOnlineGross, 10);
        Assert.Equal(1_466, onlineSliceDiscount);
        Assert.Equal(3_000, offlineSliceDiscount);
        Assert.Equal(1_463, perMinuteOnlineDiscount);
        Assert.NotEqual(perMinuteOnlineDiscount, onlineSliceDiscount);
        Assert.Equal(44_667, charge.TotalAmountToman);
        var expectedDiscount = MoneyMath.PercentOf(charge.TotalAmountToman, 10);
        Assert.Equal(4_466, expectedDiscount);
        Assert.Equal(expectedDiscount, settlement.DiscountToman);
        var conserved = settlement.MoneyConsumedToman
            + settlement.DiscountToman
            + settlement.UncoveredToman
            + settlement.OmittedToman
            + settlement.WriteOffToman
            + (settlement.Grace?.WaivedToman ?? 0);
        Assert.Equal(charge.TotalAmountToman, conserved);
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
        AssertBill(bill, 103_334, (50, 66_667L), (10, 10_000L), (20, 26_667L));
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
        using (var scope6b = factory.Services.CreateScope())
        {
            var session = await scope6b.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.AsNoTracking()
                .SingleAsync(s => s.Id == sessionId);
            Assert.Equal(1560L, session.PrepaidBookedActiveSeconds);
        }

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
        var sessionId = await S3TestHelpers.StartSession(api, "PC-O7");
        (await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.NotNull(session.CustomerId);
        Assert.Equal(customerId, session.CustomerId);
        var display = scope.ServiceProvider.GetRequiredService<KioskDisplayService>();
        factory.Clock.SetTehran(2026, 6, 2, 20, 0);
        session = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions
            .Include(s => s.Segments).Include(s => s.Station)!.ThenInclude(st => st!.Group)
            .Include(s => s.Pauses).SingleAsync(s => s.Id == sessionId);
        var onDisplay = await display.BuildAsync(session, CancellationToken.None);
        Assert.Equal(22 * 60, onDisplay!.EstimatedRemainingSeconds);
        await ToggleAsync(op, sessionId, false);
        await using (var scope2 = factory.Services.CreateAsyncScope())
        {
            session = await scope2.ServiceProvider.GetRequiredService<AppDbContext>().Sessions
                .Include(s => s.Segments).Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .Include(s => s.Pauses).SingleAsync(s => s.Id == sessionId);
            var offDisplay = await scope2.ServiceProvider.GetRequiredService<KioskDisplayService>().BuildAsync(session, CancellationToken.None);
            Assert.Equal(30 * 60, offDisplay!.EstimatedRemainingSeconds);
        }

        factory.Clock.SetTehran(2026, 6, 2, 20, 30);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        var bill = await BillAfterStopAsync(op, api, sessionId);
        AssertBill(bill, 30_000, (30, 30_000L));
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
        (await factory.CreateClientWithKey().PostAsync("/api/internal/dead-pc-sweep", null)).EnsureSuccessStatusCode();
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
        (await op.PostAsJsonAsync(
            "/api/operator/stations/PC-O11/start-guest",
            new { prepaidKind = 1, minutes = 60, openPayAtEnd = false })).EnsureSuccessStatusCode();
        using var scope11 = factory.Services.CreateScope();
        var sessionId = await scope11.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
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
        (await op.PostAsJsonAsync(
            "/api/operator/stations/PC-O12/start-guest",
            new { prepaidKind = 1, minutes = 60, openPayAtEnd = false })).EnsureSuccessStatusCode();
        using var scope12 = factory.Services.CreateScope();
        var sessionId = await scope12.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        factory.Clock.SetTehran(2026, 6, 2, 23, 40);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        await ToggleAsync(op, sessionId, false);
        factory.Clock.SetTehran(2026, 6, 3, 0, 42);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        using (var scope12Booked = factory.Services.CreateScope())
        {
            var session = await scope12Booked.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.AsNoTracking()
                .SingleAsync(s => s.Id == sessionId);
            Assert.Equal(4380L, session.PrepaidBookedActiveSeconds);
        }

        var bill = await BillAfterStopAsync(op, api, sessionId);
        AssertBill(bill, 89_333, (10, 13_333L), (20, 20_000L), (42, 56_000L));
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
        (await op.PostAsJsonAsync(
            "/api/operator/stations/PC-O13/start-guest",
            new { prepaidKind = 1, minutes = 60, openPayAtEnd = false })).EnsureSuccessStatusCode();
        using var scope13 = factory.Services.CreateScope();
        var sessionId = await scope13.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        factory.Clock.SetTehran(2026, 6, 2, 20, 15);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        factory.Clock.SetTehran(2026, 6, 2, 20, 15);
        (await S3TestHelpers.TransferAsync(op, sessionId, "PC-O13V", expectedVersion: await RowVersionAsync(sessionId))).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 2, 20, 47);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        using (var scope13Booked = factory.Services.CreateScope())
        {
            var session = await scope13Booked.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.AsNoTracking()
                .SingleAsync(s => s.Id == sessionId);
            Assert.Equal(2880L, session.PrepaidBookedActiveSeconds);
        }

        var bill = await BillAfterStopAsync(op, api, sessionId);
        AssertBill(bill, 78_667, (15, 20_000), (32, 58_667));
    }

    private async Task<(Guid CustomerId, Guid SessionId)> L12PrepareMemberPcSessionAsync(
        string stationId,
        string mobile,
        long walletTopUpToman,
        bool graceMinuteEnabled = true,
        int onlineBilledMinutes = 30,
        int offlineBilledMinutes = 30)
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        await EnsureOfflineRatesAsync(mgr);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = await db.BranchMemberSettings.SingleAsync();
            settings.ApplyDiscountsToPc = true;
            settings.GraceMinuteEnabled = graceMinuteEnabled;
            settings.LevelLadderJson = MemberSettingsMapper.SerializeLadder(LevelLadder.Default);
            await db.SaveChangesAsync();
        }

        await S3TestHelpers.RegisterPc(api, stationId, PilotSeed.PcNormalGroupId);
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "L12", mobile });
        created.EnsureSuccessStatusCode();
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await mgr.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/opening-balance",
            new { lifetimeHours = 100L })).EnsureSuccessStatusCode();
        if (walletTopUpToman > 0)
        {
            (await mgr.PostAsJsonAsync(
                $"/api/operator/members/customers/{customerId}/wallet/top-up",
                new { method = "Cash", amountToman = walletTopUpToman, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        }

        factory.Clock.SetTehran(2026, 6, 2, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, stationId);
        (await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId })).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 2, 14, onlineBilledMinutes);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        await ToggleAsync(op, sessionId, false);
        var endHour = 14 + (onlineBilledMinutes + offlineBilledMinutes) / 60;
        var endMinute = (onlineBilledMinutes + offlineBilledMinutes) % 60;
        factory.Clock.SetTehran(2026, 6, 2, endHour, endMinute);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        return (customerId, sessionId);
    }

    /// <summary>Brief step-pc-internet-switch §246 / rr4 L-12: member level discount on toggled PC (online then offline).</summary>
    [Fact]
    public async Task L12_discount_on_toggled_pc_with_nonzero_member_level()
    {
        var (customerId, sessionId) = await L12PrepareMemberPcSessionAsync("PC-L12", "09124441212", walletTopUpToman: 1_000_000L);
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        var endUtc = factory.Clock.UtcNow.UtcDateTime;
        var (charge, settlement) = await MemberPlaySettlementPreviewAsync(factory, sessionId, customerId, endUtc);
        AssertL12SilverTenPercentSliceDiscounts(charge, settlement);
        var bill = await BillAfterStopAsync(op, api, sessionId);
        Assert.Equal(70_000, bill.GetProperty("timeGrossToman").GetInt64());
        Assert.Equal(7_000, bill.GetProperty("levelDiscountToman").GetInt64());
        Assert.Equal(63_000, bill.GetProperty("collectibleToman").GetInt64());
    }

    /// <summary>L-12 variant: 25k wallet depleted mid offline slice (AccountSettlement preview before stop).</summary>
    [Fact]
    public async Task L12_discount_on_toggled_pc_partial_wallet_runs_out_during_offline_slice()
    {
        var (customerId, sessionId) = await L12PrepareMemberPcSessionAsync(
            "PC-L12P",
            "09124441213",
            walletTopUpToman: 25_000L,
            graceMinuteEnabled: false,
            onlineBilledMinutes: 11,
            offlineBilledMinutes: 30);
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        var endUtc = factory.Clock.UtcNow.UtcDateTime;
        var (charge, settlement) = await MemberPlaySettlementPreviewAsync(factory, sessionId, customerId, endUtc);
        AssertL12PartialWalletRoundDownDiscounts(charge, settlement);
        Assert.Equal(25_000, settlement.MoneyConsumedToman);
        Assert.Equal(4_466, settlement.DiscountToman);
        Assert.Equal(15_201, settlement.WriteOffToman);
        Assert.Equal(0, settlement.UncoveredToman);
        Assert.Equal(0, settlement.OmittedToman);
        Assert.Equal(0, settlement.UpdatedAccount.MoneyBalanceToman);
        var bill = await BillAfterStopAsync(op, api, sessionId);
        Assert.Equal(44_667, bill.GetProperty("timeGrossToman").GetInt64());
        Assert.Equal(4_466, bill.GetProperty("levelDiscountToman").GetInt64());
        Assert.Equal(25_000, bill.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(25_000, bill.GetProperty("unroundedPayableToman").GetInt64());
        Assert.Equal(15_201, bill.GetProperty("writeOffToman").GetInt64());
    }
}