using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using GameClub.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class SessionTransferAcceptanceTests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _api = factory.CreateClientWithKey();

    [Fact]
    public async Task ss4_scenario_01_console_to_console_transfer()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-3", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-5", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-3", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        var transfer = await S3TestHelpers.TransferAsync(op, sessionId, "PS5-5");
        transfer.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.Include(s => s.Segments).SingleAsync(s => s.Id == sessionId);
        Assert.Equal("PS5-5", session.StationId);
        Assert.Equal(SessionStatus.Open, session.Status);
        Assert.Equal("انتقال", session.Segments.OrderBy(s => s.Id).Last().SplitReason);
        var source = await db.Stations.SingleAsync(s => s.Id == "PS5-3");
        Assert.Equal(StationOperationalState.Available, source.OperationalState);
    }

    [Fact]
    public async Task ss4_scenario_02_pc_to_pc_transfer_updates_checkpoint()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterPc(_api, "PC-1", PilotSeed.PcNormalGroupId);
        await S3TestHelpers.RegisterPc(_api, "PC-2", PilotSeed.PcVipGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PC-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 20);
        await S3TestHelpers.CheckpointAsync(_api, sessionId, S3TestHelpers.T(2026, 6, 3, 14, 20));
        factory.Clock.SetTehran(2026, 6, 3, 14, 25);
        (await S3TestHelpers.TransferAsync(op, sessionId, "PC-2")).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.Include(s => s.Segments).SingleAsync(s => s.Id == sessionId);
        Assert.Equal("PC-2", session.StationId);
        Assert.Equal(PilotSeed.PcVipGroupId, session.Segments.Last().GroupId);
        Assert.Equal(factory.Clock.UtcNow.UtcDateTime, session.LastCheckpointUtc);
    }

    [Fact]
    public async Task ss4_scenario_03_cross_type_refused_without_db_change()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-A", 1);
        await S3TestHelpers.RegisterPc(_api, "PC-A", PilotSeed.PcNormalGroupId);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-A", 1);
        var response = await S3TestHelpers.TransferAsync(op, sessionId, "PC-A");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("PS5-A", (await db.Sessions.SingleAsync()).StationId);
        Assert.Single(await db.Segments.ToListAsync());
    }

    [Fact]
    public async Task ss4_scenario_04_transfer_does_not_settle_invoice()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-2", 1);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        (await S3TestHelpers.TransferAsync(op, sessionId, "PS5-2")).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Invoices.AnyAsync());
        Assert.False(await db.PendingBills.AnyAsync());
    }

    [Fact]
    public async Task ss4_scenario_05_target_segment_uses_target_tariff_group()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-2", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        (await S3TestHelpers.TransferAsync(op, sessionId, "PS5-2")).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.Include(s => s.Segments).SingleAsync(s => s.Id == sessionId);
        Assert.Equal(GameTimerReference.Ps5GroupId, session.Segments.First().GroupId);
        Assert.Equal(GameTimerReference.Ps5GroupId, session.Segments.Last().GroupId);
        Assert.Equal("PS5-2", session.Segments.Last().StationId);
        Assert.Equal("انتقال", session.Segments.Last().SplitReason);
    }

    [Fact]
    public async Task ss4_scenario_06_buffet_stays_with_session()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-2", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = CatalogSeed.SodaId });
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        (await S3TestHelpers.TransferAsync(op, sessionId, "PS5-2")).EnsureSuccessStatusCode();
        var bill = await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        bill.EnsureSuccessStatusCode();
        var body = await bill.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(15_000, body.GetProperty("buffetGrossToman").GetInt64());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sale = await db.OpenSales.SingleAsync();
        Assert.Equal(sessionId, sale.SessionId);
        Assert.Equal("PS5-2", sale.StationId);
    }

    [Fact]
    public async Task ss4_scenario_07_fault_path_marks_source_broken()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-3", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-5", 1);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-3", 1);
        (await S3TestHelpers.TransferAsync(op, sessionId, "PS5-5", markSourceBroken: true)).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(StationOperationalState.Broken, (await db.Stations.SingleAsync(s => s.Id == "PS5-3")).OperationalState);
    }

    [Fact]
    public async Task ss4_scenario_08_ordinary_path_frees_source()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-2", 1);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        (await S3TestHelpers.TransferAsync(op, sessionId, "PS5-2")).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = await db.Stations.SingleAsync(s => s.Id == "PS5-1");
        Assert.Equal(StationOperationalState.Available, source.OperationalState);
        Assert.False(await db.Sessions.AnyAsync(s => s.StationId == "PS5-1" && s.Status != SessionStatus.Closed));
    }

    [Fact]
    public async Task ss4_scenario_09_history_immutable_segment_sum_pricing()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-2", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        (await S3TestHelpers.TransferAsync(op, sessionId, "PS5-2")).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.Include(s => s.Segments).SingleAsync(s => s.Id == sessionId);
        Assert.Equal(2, session.Segments.Count);
        Assert.Equal("PS5-1", session.Segments.First().StationId);
        Assert.NotNull(session.Segments.First().EndUtc);
        var billing = scope.ServiceProvider.GetRequiredService<SessionBilling>();
        var amount = (await billing.BuildChargeAsync(session, factory.Clock.UtcNow.UtcDateTime)).TotalAmountToman;
        Assert.True(amount > 0);
    }

    [Fact]
    public async Task ss4_scenario_10_transfer_does_not_inflate_billed_minutes()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-2", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.Sessions.Include(s => s.Segments).SingleAsync();
            var billing = scope.ServiceProvider.GetRequiredService<SessionBilling>();
            var unsplitMinutes = (await billing.BuildChargeAsync(session, factory.Clock.UtcNow.UtcDateTime)).TotalBilledMinutes;
            (await S3TestHelpers.TransferAsync(op, sessionId, "PS5-2")).EnsureSuccessStatusCode();
            session = await db.Sessions.Include(s => s.Segments).AsNoTracking().SingleAsync();
            var afterMinutes = (await billing.BuildChargeAsync(session, factory.Clock.UtcNow.UtcDateTime)).TotalBilledMinutes;
            Assert.Equal(unsplitMinutes, afterMinutes);
        }
    }

    [Fact]
    public async Task ss4_scenario_11_many_transfers_not_capped_at_ten_segments()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        for (var i = 0; i < 13; i++)
        {
            await S3TestHelpers.RegisterConsole(_api, op, $"PS5-{i}", 1);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-0", 1);
        for (var i = 1; i <= 12; i++)
        {
            factory.Clock.SetTehran(2026, 6, 3, 14, i, 0);
            (await S3TestHelpers.TransferAsync(op, sessionId, $"PS5-{i}")).EnsureSuccessStatusCode();
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.Segments.CountAsync(s => s.SessionId == sessionId);
        Assert.Equal(13, count);
    }

    [Fact]
    public async Task ss4_scenario_12_transfer_split_reason_and_audit()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-2", 1);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        (await S3TestHelpers.TransferAsync(op, sessionId, "PS5-2")).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var last = await db.Segments.Where(s => s.SessionId == sessionId).OrderBy(s => s.Id).LastAsync();
        Assert.Equal("انتقال", last.SplitReason);
        Assert.True(await db.AuditLog.AnyAsync(a => a.Action == "session.transfer"));
    }

    [Fact]
    public async Task ss4_scenario_13_ineligible_target_occupied_refused()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-2", 1);
        var s1 = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        _ = await S3TestHelpers.StartSession(_api, "PS5-2", 1);
        Assert.Equal(HttpStatusCode.Conflict, (await S3TestHelpers.TransferAsync(op, s1, "PS5-2")).StatusCode);
    }

    [Fact]
    public async Task ss4_scenario_14_broken_target_refused()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-2", 1);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var target = await db.Stations.SingleAsync(s => s.Id == "PS5-2");
            target.OperationalState = StationOperationalState.Broken;
            await db.SaveChangesAsync();
        }

        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        Assert.Equal(HttpStatusCode.Conflict, (await S3TestHelpers.TransferAsync(op, sessionId, "PS5-2")).StatusCode);
    }

    [Fact]
    public async Task ss4_scenario_15_stale_version_and_idempotent_command()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-2", 1);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        var cmd = Guid.NewGuid();
        var first = await S3TestHelpers.TransferAsync(op, sessionId, "PS5-2", commandId: cmd);
        first.EnsureSuccessStatusCode();
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        var rv = body.GetProperty("rowVersion").GetUInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await S3TestHelpers.TransferAsync(op, sessionId, "PS5-2", expectedVersion: rv - 1)).StatusCode);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-3", 1);
        var second = await S3TestHelpers.TransferAsync(op, sessionId, "PS5-3", commandId: cmd);
        second.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.Segments.CountAsync(s => s.SessionId == sessionId));
    }

    [Fact]
    public async Task ss4_paused_console_source_resumes_on_target()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-2", 1);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/pause", null);
        (await S3TestHelpers.TransferAsync(op, sessionId, "PS5-2")).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.Include(s => s.Pauses).SingleAsync();
        Assert.Equal(SessionStatus.Open, session.Status);
        Assert.All(session.Pauses, p => Assert.NotNull(p.EndUtc));
    }
}
