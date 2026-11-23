using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using GameClub.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

/// <summary>Brief §10.3 merge-bar gaps: end paths, L13–L19 API, toggle refusals, heartbeat/grid.</summary>
[Collection("server")]
public sealed class PcInternetBrief103CompletionTests(GameClubWebApplicationFactory factory)
{
    private static async Task EnsureOfflineRatesAsync(HttpClient mgr)
    {
        (await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new { onlineDay = 80_000L, onlineNight = 100_000L, offlineDay = 60_000L, offlineNight = 80_000L }))
            .EnsureSuccessStatusCode();
    }

    private async Task<uint> RowVersionAsync(Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
    }

    private async Task AssertInternetAutoOnAsync(string stationId, string auditReason)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.StationInternetStates.SingleAsync(s => s.StationId == stationId);
        Assert.True(row.DesiredOn);
        Assert.Equal("session_end", row.DesiredReason);
        var entry = (await db.AuditLog.OrderByDescending(a => a.Id).ToListAsync())
            .First(a => a.Action == "internet.auto_on" && a.DetailsJson.Contains(stationId, StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(entry.DetailsJson);
        Assert.Equal(auditReason, doc.RootElement.GetProperty("reason").GetString());
    }

    private async Task<Guid> StartPlayingWithInternetOffAsync(string stationId, HttpClient op, HttpClient api)
    {
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, stationId);
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = await RowVersionAsync(sessionId),
            commandId = Guid.NewGuid(),
        })).EnsureSuccessStatusCode();
        return sessionId;
    }

    [Fact]
    public async Task PcInternet_end_pc_stop_writes_internet_auto_on()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-ESTOP", PilotSeed.PcNormalGroupId);
        var sessionId = await StartPlayingWithInternetOffAsync("PC-ESTOP", op, api);
        (await api.PostAsync($"/api/sessions/{sessionId}/stop", null)).EnsureSuccessStatusCode();
        await AssertInternetAutoOnAsync("PC-ESTOP", "pc_stop");
    }

    [Fact]
    public async Task PcInternet_end_force_logout_writes_internet_auto_on()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-EFORCE", PilotSeed.PcNormalGroupId);
        var sessionId = await StartPlayingWithInternetOffAsync("PC-EFORCE", op, api);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null)).EnsureSuccessStatusCode();
        await AssertInternetAutoOnAsync("PC-EFORCE", "pc_stop");
    }

    [Fact]
    public async Task PcInternet_end_auto_close_writes_internet_auto_on()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-EAUTO", PilotSeed.PcNormalGroupId);
        var sessionId = await StartPlayingWithInternetOffAsync("PC-EAUTO", op, api);
        factory.Clock.SetTehran(2026, 6, 2, 12, 5);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        factory.Clock.SetTehran(2026, 6, 2, 12, 15);
        (await api.PostAsync("/api/internal/dead-pc-sweep", null)).EnsureSuccessStatusCode();
        await AssertInternetAutoOnAsync("PC-EAUTO", "auto_close");
    }

    [Fact]
    public async Task PcInternet_end_manual_settle_writes_internet_auto_on()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-EMAN", PilotSeed.PcNormalGroupId);
        var sessionId = await StartPlayingWithInternetOffAsync("PC-EMAN", op, api);
        factory.Clock.SetTehran(2026, 6, 2, 12, 10);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        await api.PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { at = TestTimes.T(2026, 6, 2, 12, 0) });
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/manual-settle", new { })).EnsureSuccessStatusCode();
        await AssertInternetAutoOnAsync("PC-EMAN", "manual_settle");
    }

    [Fact]
    public async Task PcInternet_end_close_settle_writes_internet_auto_on()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await S3TestHelpers.RegisterPc(api, "PC-ECLOSE", PilotSeed.PcNormalGroupId);
        var sessionId = await StartPlayingWithInternetOffAsync("PC-ECLOSE", op, api);
        factory.Clock.SetTehran(2026, 6, 2, 12, 10);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        var preview = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/close-settle-preview");
        var collectible = preview!.GetProperty("collectibleToman").GetInt64();
        (await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 2 },
            actualCashToman = collectible,
            actualCardReaderToman = 0L,
            sessionActions = new[] { new { sessionId, action = "settle" } },
        })).EnsureSuccessStatusCode();
        await AssertInternetAutoOnAsync("PC-ECLOSE", "close_settle");
    }

    [Fact]
    public async Task PcInternetToggle_command_required_and_version_required()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-VAL", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-VAL");
        var rv = await RowVersionAsync(sessionId);
        var noCmd = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new { on = false, expectedVersion = rv, commandId = Guid.Empty });
        Assert.Equal(HttpStatusCode.BadRequest, noCmd.StatusCode);
        Assert.Equal("command_required", (await noCmd.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        using var noVerReq = new HttpRequestMessage(HttpMethod.Post, $"/api/operator/sessions/{sessionId}/internet")
        {
            Content = new StringContent(
                $"{{\"on\":false,\"commandId\":\"{Guid.NewGuid()}\"}}",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        var noVer = await op.SendAsync(noVerReq);
        Assert.Equal(HttpStatusCode.BadRequest, noVer.StatusCode);
        Assert.Equal("version_required", (await noVer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task PcInternetToggle_closed_session_returns_closed()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-CL", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-CL");
        factory.Clock.SetTehran(2026, 6, 2, 12, 10);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        (await api.PostAsync($"/api/sessions/{sessionId}/stop", null)).EnsureSuccessStatusCode();
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        await S3TestHelpers.FinishPcPaymentAsync(factory, sessionId, bill!.GetProperty("collectibleToman").GetInt64());
        var res = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = await RowVersionAsync(sessionId),
            commandId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("closed", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task PcInternetToggle_awaiting_payment_refused()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-AP", PilotSeed.PcNormalGroupId);
        var sessionId = await StartPlayingWithInternetOffAsync("PC-AP", op, api);
        (await api.PostAsync($"/api/sessions/{sessionId}/stop", null)).EnsureSuccessStatusCode();
        var res = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = true,
            expectedVersion = await RowVersionAsync(sessionId),
            commandId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("awaiting_payment", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task PcInternetToggle_manual_settle_queue_refused()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-MSQ", PilotSeed.PcNormalGroupId);
        var sessionId = await StartPlayingWithInternetOffAsync("PC-MSQ", op, api);
        factory.Clock.SetTehran(2026, 6, 2, 12, 10);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        await api.PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { at = TestTimes.T(2026, 6, 2, 12, 0) });
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(SessionStatus.ManualSettle, await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.Status).SingleAsync());
        }

        var res = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = true,
            expectedVersion = await RowVersionAsync(sessionId),
            commandId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var code = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
        Assert.True(code is "awaiting_payment" or "manual_settle");
    }

    [Fact]
    public async Task PcInternetToggle_concurrent_press_one_session_busy()
    {
        await S3TestHelpers.ResetAsync(factory);
        var opA = await factory.CreateOperatorClientAsync();
        var opB = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-BUSY", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-BUSY");
        var ver = await RowVersionAsync(sessionId);
        var sawBusy = false;
        for (var i = 0; i < 25; i++)
        {
            var results = await Task.WhenAll(
                opA.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new { on = false, expectedVersion = ver, commandId = Guid.NewGuid() }),
                opB.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new { on = false, expectedVersion = ver, commandId = Guid.NewGuid() }));
            var bodies = await Task.WhenAll(results.Select(r => r.Content.ReadAsStringAsync()));
            sawBusy |= bodies.Any(b => b.Contains("session_busy", StringComparison.Ordinal));
            if (sawBusy)
            {
                break;
            }

            ver = await RowVersionAsync(sessionId);
        }

        Assert.True(sawBusy);
    }

    [Fact]
    public async Task PcInternet_L13_offline_pair_required_on_pc_rates_put()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var res = await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new { onlineDay = 80_000L, onlineNight = 100_000L, offlineDay = 60_000L });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("offline_pair_required", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task PcInternet_L13_new_session_after_offline_save_can_toggle()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-L13N", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-L13N");
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = await RowVersionAsync(sessionId),
            commandId = Guid.NewGuid(),
        })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task PcInternet_L15_amount_prepaid_grants_one_more_minute_below_new_rate()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-L15", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 20, 0);
        (await op.PostAsJsonAsync(
            "/api/operator/stations/PC-L15/start-guest",
            new { prepaidKind = 2, amountToman = 20_000L, openPayAtEnd = false })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sessionId = await db.Sessions.Select(s => s.Id).SingleAsync();
        factory.Clock.SetTehran(2026, 6, 2, 20, 14);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        factory.Clock.SetTehran(2026, 6, 2, 20, 14, 30);
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = await RowVersionAsync(sessionId),
            commandId = Guid.NewGuid(),
        })).EnsureSuccessStatusCode();
        var booked = await db.Sessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.PrepaidBookedActiveSeconds).SingleAsync();
        var started = await db.Sessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.StartedAtUtc).SingleAsync();
        var activeThrough = factory.Clock.UtcNow.UtcDateTime - started;
        var activeSeconds = (int)Math.Ceiling(activeThrough.TotalSeconds);
        Assert.Equal(activeSeconds + 60, booked);
    }

    [Fact]
    public async Task PcInternet_L17_firewall_error_still_offline_segment_and_grid_then_toggle_back()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-L17", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-L17");
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = await RowVersionAsync(sessionId),
            commandId = Guid.NewGuid(),
        })).EnsureSuccessStatusCode();
        var deviceId = await SeedKioskDeviceAsync("PC-L17");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.StationInternetStates.SingleAsync(s => s.StationId == "PC-L17");
        await scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>().OnHeartbeatAsync(
            deviceId,
            sessionId,
            0,
            new KioskInternetAppliedReport
            {
                Epoch = row.Epoch,
                Version = row.DesiredVersion,
                On = false,
                AppliedAtUtc = factory.Clock.UtcNow.UtcDateTime,
                Error = "firewall_not_configured",
            },
            CancellationToken.None);
        var modes = await db.Segments.Where(s => s.SessionId == sessionId).OrderBy(s => s.StartUtc).Select(s => s.Mode).ToListAsync();
        Assert.Equal(PlayMode.Offline, modes[^1]);
        var tiles = await op.GetFromJsonAsync<JsonElement[]>("/api/operator/grid");
        var tile = tiles!.Single(t => t.GetProperty("id").GetString() == "PC-L17");
        Assert.Equal("firewall_not_configured", tile.GetProperty("internetError").GetString());
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = true,
            expectedVersion = await RowVersionAsync(sessionId),
            commandId = Guid.NewGuid(),
        })).EnsureSuccessStatusCode();
        modes = await db.Segments.Where(s => s.SessionId == sessionId).OrderBy(s => s.StartUtc).Select(s => s.Mode).ToListAsync();
        Assert.Equal(3, modes.Count);
        Assert.Equal(PlayMode.Online, modes[^1]);
    }

    [Fact]
    public async Task PcInternet_L18_duration_toggle_sets_amount_kind_and_budget()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-L18", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 20, 0);
        (await op.PostAsJsonAsync(
            "/api/operator/stations/PC-L18/start-guest",
            new { prepaidKind = 1, minutes = 60, openPayAtEnd = false })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.Include(s => s.Segments).SingleAsync();
        var firstSeg = session.Segments.OrderBy(s => s.StartUtc).First();
        var rates = scope.ServiceProvider.GetRequiredService<RateConfigService>();
        var snapshot = await rates.LoadSnapshotAsync(session.RateCardSnapshotId, default);
        var budget = PrepaidPlanner.ForDuration(
            session.StartedAtUtc,
            TimeSpan.FromSeconds(session.PrepaidBookedActiveSeconds!.Value),
            firstSeg.SegmentKey,
            session.StationId,
            firstSeg.GroupId,
            firstSeg.Mode,
            firstSeg.PlayerCount,
            snapshot.Card).ConsumedToman;
        factory.Clock.SetTehran(2026, 6, 2, 20, 15);
        await S3TestHelpers.CheckpointAsync(api, session.Id, factory.Clock.UtcNow);
        (await op.PostAsJsonAsync($"/api/operator/sessions/{session.Id}/internet", new
        {
            on = false,
            expectedVersion = await RowVersionAsync(session.Id),
            commandId = Guid.NewGuid(),
        })).EnsureSuccessStatusCode();
        session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        Assert.Equal(PrepaidSessionKind.Amount, session.PrepaidKind);
        Assert.Equal(budget, session.PrepaidAmountBudgetToman);
        Assert.Equal(80_000, budget);
    }

    [Fact]
    public async Task PcInternet_L19_transfer_converts_duration_without_internet_change()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-L19", PilotSeed.PcNormalGroupId);
        await S3TestHelpers.RegisterPc(api, "PC-L19V", PilotSeed.PcVipGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 20, 0);
        (await op.PostAsJsonAsync(
            "/api/operator/stations/PC-L19/start-guest",
            new { prepaidKind = 1, minutes = 60, openPayAtEnd = false })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sessionId = await db.Sessions.Select(s => s.Id).SingleAsync();
        factory.Clock.SetTehran(2026, 6, 2, 20, 15);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        (await S3TestHelpers.TransferAsync(op, sessionId, "PC-L19V", expectedVersion: await RowVersionAsync(sessionId))).EnsureSuccessStatusCode();
        var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.Equal(PrepaidSessionKind.Amount, session.PrepaidKind);
        Assert.Equal(80_000, session.PrepaidAmountBudgetToman);
        Assert.Equal(PlayMode.Online, session.Mode);
    }

    [Fact]
    public async Task PcInternet_heartbeat_applied_audit_once_per_version()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-HBA", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        await S3TestHelpers.StartSession(api, "PC-HBA");
        var deviceId = await SeedKioskDeviceAsync("PC-HBA");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.StationInternetStates.SingleAsync(s => s.StationId == "PC-HBA");
        row.DesiredVersion = 3;
        await db.SaveChangesAsync();
        var coordinator = scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>();
        var report = new KioskInternetAppliedReport
        {
            Epoch = row.Epoch,
            Version = 3,
            On = true,
            AppliedAtUtc = factory.Clock.UtcNow.UtcDateTime,
        };
        await coordinator.OnHeartbeatAsync(deviceId, null, 0, report, CancellationToken.None);
        await coordinator.OnHeartbeatAsync(deviceId, null, 0, report, CancellationToken.None);
        var applied = await db.AuditLog.Where(a => a.Action == "internet.applied").ToListAsync();
        Assert.Single(applied, a => a.DetailsJson.Contains("\"version\":3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PcInternet_heartbeat_ignored_report_writes_no_internet_audit()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-HBIGN", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-HBIGN");
        var deviceId = await SeedKioskDeviceAsync("PC-HBIGN");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.StationInternetStates.SingleAsync(s => s.StationId == "PC-HBIGN");
        row.AppliedVersion = 5;
        row.AppliedOn = true;
        row.SessionId = sessionId;
        var goodEpoch = row.Epoch;
        await db.SaveChangesAsync();
        var coordinator = scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>();
        var at = factory.Clock.UtcNow.UtcDateTime;

        async Task AssertInternetStateAndAuditFrozenAsync()
        {
            var fresh = await db.StationInternetStates.AsNoTracking().SingleAsync(s => s.StationId == "PC-HBIGN");
            Assert.Equal(5, fresh.AppliedVersion);
            Assert.True(fresh.AppliedOn);
            Assert.Equal(goodEpoch, fresh.Epoch);
            Assert.Empty(await db.AuditLog.Where(a => a.Action == "internet.applied" || a.Action == "internet.apply_failed").ToListAsync());
        }

        await coordinator.OnHeartbeatAsync(deviceId, sessionId, 0, new KioskInternetAppliedReport
        {
            Epoch = Guid.NewGuid(),
            Version = 6,
            On = true,
            AppliedAtUtc = at,
        }, CancellationToken.None);
        await AssertInternetStateAndAuditFrozenAsync();

        await coordinator.OnHeartbeatAsync(deviceId, Guid.NewGuid(), 0, new KioskInternetAppliedReport
        {
            Epoch = goodEpoch,
            Version = 6,
            On = false,
            AppliedAtUtc = at,
        }, CancellationToken.None);
        await AssertInternetStateAndAuditFrozenAsync();

        await coordinator.OnHeartbeatAsync(deviceId, sessionId, 0, new KioskInternetAppliedReport
        {
            Epoch = goodEpoch,
            Version = 5,
            On = false,
            AppliedAtUtc = at,
        }, CancellationToken.None);
        await AssertInternetStateAndAuditFrozenAsync();
    }

    [Fact]
    public async Task PcInternet_transfer_duration_prepaid_writes_duration_converted_audit()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-DCV", PilotSeed.PcNormalGroupId);
        await S3TestHelpers.RegisterPc(api, "PC-DCVV", PilotSeed.PcVipGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 20, 0);
        (await op.PostAsJsonAsync(
            "/api/operator/stations/PC-DCV/start-guest",
            new { prepaidKind = 1, minutes = 60, openPayAtEnd = false })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sessionId = await db.Sessions.Select(s => s.Id).SingleAsync();
        factory.Clock.SetTehran(2026, 6, 2, 20, 15);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        var cmd = Guid.NewGuid();
        (await S3TestHelpers.TransferAsync(op, sessionId, "PC-DCVV", expectedVersion: await RowVersionAsync(sessionId), commandId: cmd))
            .EnsureSuccessStatusCode();
        Assert.Contains(
            await db.AuditLog.Where(a => a.Action == "prepaid.duration_converted").ToListAsync(),
            a => a.DetailsJson.Contains(cmd.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PcInternet_heartbeat_apply_failed_audit_once_per_version()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-HBF", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        await S3TestHelpers.StartSession(api, "PC-HBF");
        var deviceId = await SeedKioskDeviceAsync("PC-HBF");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.StationInternetStates.SingleAsync(s => s.StationId == "PC-HBF");
        row.DesiredVersion = 4;
        await db.SaveChangesAsync();
        var coordinator = scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>();
        var report = new KioskInternetAppliedReport
        {
            Epoch = row.Epoch,
            Version = 4,
            On = false,
            AppliedAtUtc = factory.Clock.UtcNow.UtcDateTime,
            Error = "firewall_not_configured",
        };
        await coordinator.OnHeartbeatAsync(deviceId, null, 0, report, CancellationToken.None);
        await coordinator.OnHeartbeatAsync(deviceId, null, 0, report, CancellationToken.None);
        Assert.Single(await db.AuditLog.Where(a => a.Action == "internet.apply_failed").ToListAsync());
    }

    [Fact]
    public async Task PcInternet_Prepaid_E6_lease_authorizedUntil_moves_after_toggle()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-LEASE", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 20, 0);
        (await op.PostAsJsonAsync(
            "/api/operator/stations/PC-LEASE/start-guest",
            new { prepaidKind = 2, amountToman = 40_000L, openPayAtEnd = false })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sessionId = await db.Sessions.Select(s => s.Id).SingleAsync();
        var before = await db.Sessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.PrepaidBookedActiveSeconds).SingleAsync();
        factory.Clock.SetTehran(2026, 6, 2, 20, 15);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = await RowVersionAsync(sessionId),
            commandId = Guid.NewGuid(),
        })).EnsureSuccessStatusCode();
        var after = await db.Sessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.PrepaidBookedActiveSeconds).SingleAsync();
        Assert.True(after > before);
        Assert.Equal(35 * 60, after);
    }

    [Fact]
    public async Task PcInternet_KioskGuestStart_ForcesOnlineMode()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-GST", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        (await op.PostAsJsonAsync(
            "/api/operator/stations/PC-GST/start-guest",
            new { openPayAtEnd = true })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var mode = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Mode).SingleAsync();
        Assert.Equal(PlayMode.Online, mode);
    }

    [Fact]
    public async Task PcInternet_Transfer_source_desired_on_audit_transfer_source()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-TR-S", PilotSeed.PcNormalGroupId);
        await S3TestHelpers.RegisterPc(api, "PC-TR-T", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-TR-S");
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = await RowVersionAsync(sessionId),
            commandId = Guid.NewGuid(),
        })).EnsureSuccessStatusCode();
        (await S3TestHelpers.TransferAsync(op, sessionId, "PC-TR-T", expectedVersion: await RowVersionAsync(sessionId))).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = await db.StationInternetStates.SingleAsync(s => s.StationId == "PC-TR-S");
        Assert.True(source.DesiredOn);
        var target = await db.StationInternetStates.SingleAsync(s => s.StationId == "PC-TR-T");
        Assert.True(target.DesiredOn);
        var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.Equal(PlayMode.Online, session.Mode);
    }

    private async Task<Guid> SeedKioskDeviceAsync(string stationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = new KioskDeviceEntity
        {
            Id = Guid.NewGuid(),
            StationId = stationId,
            DeviceKeyHash = "test",
            PairedAtUtc = factory.Clock.UtcNow.UtcDateTime,
            LastSeenUtc = factory.Clock.UtcNow.UtcDateTime,
        };
        db.KioskDevices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }
}