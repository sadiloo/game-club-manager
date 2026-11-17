using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class Ss14FixR1QaTests(GameClubWebApplicationFactory factory)
{
    private static readonly object[] Fc27Rates =
    [
        new { mode = "Offline", playerCount = (int?)1, hourlyToman = 150_000L, nightHourlyToman = 180_000L },
        new { mode = "Offline", playerCount = (int?)2, hourlyToman = 170_000L },
    ];

    [Fact]
    public async Task FixR1_F2_invalid_hourly_rate_creates_nothing_no_duplicate_on_retry()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var body = new { name = "BrokenGame", enabled = true, rates = new[] { new { mode = "Offline", playerCount = (int?)1, hourlyToman = 0L, nightHourlyToman = 180_000L } } };
        var bad = await mgr.PostAsJsonAsync("/api/operator/game-titles", body);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var badBody = await bad.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("نرخ ساعتی بازی", badBody.GetProperty("message").GetString()!, StringComparison.Ordinal);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.GameTitles.AnyAsync());
            Assert.False(await db.AuditLog.AnyAsync(a => a.Action.StartsWith("game_title")));
        }

        var retry = await mgr.PostAsJsonAsync("/api/operator/game-titles", body);
        Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
        Assert.NotEqual(HttpStatusCode.Conflict, retry.StatusCode);
    }

    [Fact]
    public async Task FixR1_F3_unknown_console_option_returns_400_not_500()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var body = new
        {
            name = "BadOpt",
            enabled = true,
            rates = new object[]
            {
                new { mode = "Vr", playerCount = (int?)1, hourlyToman = 150_000L },
                new { mode = "Offline", playerCount = (int?)1, hourlyToman = 150_000L },
            },
        };
        var res = await mgr.PostAsJsonAsync("/api/operator/game-titles", body);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var err = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("این گزینه در نرخ ایستگاه کنسول", err.GetProperty("message").GetString()!, StringComparison.Ordinal);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.GameTitles.AnyAsync());
    }

    [Fact]
    public async Task FixR1_F3_duplicate_option_rows_return_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var body = new
        {
            name = "DupRows",
            enabled = true,
            rates = new object[]
            {
                new { mode = "Offline", playerCount = (int?)1, hourlyToman = 150_000L },
                new { mode = "Offline", playerCount = (int?)1, hourlyToman = 160_000L },
            },
        };
        var dup = await mgr.PostAsJsonAsync("/api/operator/game-titles", body);
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
        var dupMsg = await dup.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("برای یک گزینه کنسول", dupMsg.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixR1_N3_invalid_rate_mode_returns_400_persian_not_500()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var res = await mgr.PostAsJsonAsync(
            "/api/operator/game-titles",
            new
            {
                name = "BadMode",
                enabled = true,
                rates = new[] { new { mode = "NotAPlayMode", playerCount = (int?)1, hourlyToman = 150_000L } },
            });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("حالت بازی نامعتبر", json.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixR1_F2_invalid_night_rate_message_persian()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var res = await mgr.PostAsJsonAsync(
            "/api/operator/game-titles",
            new
            {
                name = "BadNight",
                enabled = true,
                rates = new[] { new { mode = "Offline", playerCount = (int?)1, hourlyToman = 150_000L, nightHourlyToman = 0L } },
            });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("نرخ شبانه بازی", json.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixR1_F2_audit_failure_rolls_back_title_create()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE OR REPLACE FUNCTION test_block_game_title_audit()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW.action LIKE 'game_%' THEN
                    RAISE EXCEPTION 'test_block_game_title_audit';
                  END IF;
                  RETURN NEW;
                END;
                $$;
                DROP TRIGGER IF EXISTS test_block_game_title_audit ON audit_log;
                CREATE TRIGGER test_block_game_title_audit
                  BEFORE INSERT ON audit_log
                  FOR EACH ROW EXECUTE FUNCTION test_block_game_title_audit();
                """);
        }

        try
        {
            var res = await mgr.PostAsJsonAsync(
                "/api/operator/game-titles",
                new { name = "TxRoll", enabled = true, rates = Fc27Rates });
            Assert.NotEqual(HttpStatusCode.Created, res.StatusCode);
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.GameTitles.AnyAsync(t => t.Name == "TxRoll"));
            Assert.False(await db.GameRateLines.AnyAsync());
            Assert.False(await db.AuditLog.AnyAsync(a => a.Action.StartsWith("game_")));
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                """
                DROP TRIGGER IF EXISTS test_block_game_title_audit ON audit_log;
                DROP FUNCTION IF EXISTS test_block_game_title_audit();
                """);
        }
    }

    [Fact]
    public async Task FixR1e_F2_second_save_failure_rolls_back_title_update_rates_and_audit()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var create = await mgr.PostAsJsonAsync(
            "/api/operator/game-titles",
            new { name = "BeforeName", enabled = true, rates = Fc27Rates });
        create.EnsureSuccessStatusCode();
        var gameId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        int auditBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            auditBefore = await db.AuditLog.CountAsync(a => a.Action.StartsWith("game_"));
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE OR REPLACE FUNCTION test_block_game_title_audit()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW.action LIKE 'game_%' THEN
                    RAISE EXCEPTION 'test_block_game_title_audit';
                  END IF;
                  RETURN NEW;
                END;
                $$;
                DROP TRIGGER IF EXISTS test_block_game_title_audit ON audit_log;
                CREATE TRIGGER test_block_game_title_audit
                  BEFORE INSERT ON audit_log
                  FOR EACH ROW EXECUTE FUNCTION test_block_game_title_audit();
                """);
        }

        try
        {
            var res = await mgr.PutAsJsonAsync(
                $"/api/operator/game-titles/{gameId}",
                new
                {
                    name = "AfterName",
                    enabled = false,
                    rates = new[]
                    {
                        new { mode = "Offline", playerCount = (int?)1, hourlyToman = 999_000L },
                        new { mode = "Offline", playerCount = (int?)2, hourlyToman = 888_000L },
                    },
                });
            Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var title = await db.GameTitles.Include(g => g.RateLines).SingleAsync(g => g.Id == gameId);
            Assert.Equal("BeforeName", title.Name);
            Assert.True(title.Enabled);
            var line1 = title.RateLines.Single(r => r.PlayerCount == 1);
            Assert.Equal(150_000L, line1.HourlyToman);
            Assert.Equal(180_000L, line1.NightHourlyToman);
            Assert.Equal(auditBefore, await db.AuditLog.CountAsync(a => a.Action.StartsWith("game_")));
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                """
                DROP TRIGGER IF EXISTS test_block_game_title_audit ON audit_log;
                DROP FUNCTION IF EXISTS test_block_game_title_audit();
                """);
        }
    }

    [Fact]
    public async Task FixR1_F4_bill_and_invoice_carry_persian_game_line_with_snapshot()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        await S3TestHelpers.RegisterConsole(factory.CreateClientWithKey(), op, "PS5-R1", 2);
        var create = await mgr.PostAsJsonAsync("/api/operator/game-titles", new { name = "FC27", enabled = true, rates = Fc27Rates });
        create.EnsureSuccessStatusCode();
        var gameId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(factory.CreateClientWithKey(), "PS5-R1", 2);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.Sessions.Include(s => s.Segments).SingleAsync(s => s.Id == sessionId);
            await op.PostAsJsonAsync(
                $"/api/operator/sessions/{sessionId}/game",
                new { gameTitleId = gameId, expectedVersion = session.RowVersion, commandId = Guid.NewGuid() });
        }
        factory.Clock.SetTehran(2026, 8, 8, 15, 0);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        var gameSeg = bill.GetProperty("segments").EnumerateArray()
            .First(s => s.TryGetProperty("gameTitleName", out var gn) && gn.GetString() == "FC27");
        var display = gameSeg.GetProperty("displayLine").GetString();
        Assert.StartsWith("FC", display, StringComparison.Ordinal);
        Assert.Contains("۱۷۰٬۰۰۰", display, StringComparison.Ordinal);
        Assert.Contains("۱۴:۰۰", display, StringComparison.Ordinal);

        var collectible = bill.GetProperty("collectibleToman").GetInt64();
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new
        {
            payments = new[] { new { method = "Cash", amountToman = collectible } },
        })).EnsureSuccessStatusCode();
        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db2.Invoices.Include(i => i.TimeLines).SingleAsync(i => i.SessionId == sessionId);
        Assert.Contains(invoice.TimeLines, t => t.GameTitleNameSnapshot == "FC27");
        var apiInv = await op.GetFromJsonAsync<JsonElement>($"/api/operator/invoices/{invoice.Number}");
        Assert.True(apiInv.TryGetProperty("timeLines", out var lines));
        Assert.Contains(lines.EnumerateArray(), l => l.GetProperty("gameTitleName").GetString() == "FC27");
    }

    [Fact]
    public async Task FixR1_F5_zero_minute_game_segment_shows_game_rate_on_bill()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        await S3TestHelpers.RegisterConsole(factory.CreateClientWithKey(), op, "PS5-Z0", 1);
        var create = await mgr.PostAsJsonAsync("/api/operator/game-titles", new { name = "FC27", enabled = true, rates = Fc27Rates });
        create.EnsureSuccessStatusCode();
        var gameId = (await create.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetInt32();
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(factory.CreateClientWithKey(), "PS5-Z0", 1);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ver = await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
            await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/game", new { gameTitleId = gameId, expectedVersion = ver, commandId = Guid.NewGuid() });
        }
        factory.Clock.SetTehran(2026, 8, 8, 14, 0, 5);
        var ver2 = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions
            .Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/game", new { gameTitleId = (int?)null, expectedVersion = ver2, commandId = Guid.NewGuid() });
        factory.Clock.SetTehran(2026, 8, 8, 14, 20);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        var gameSeg = bill.GetProperty("segments").EnumerateArray()
            .First(s => s.TryGetProperty("gameTitleName", out var n) && n.GetString() == "FC27");
        Assert.Equal(150_000, gameSeg.GetProperty("hourlyToman").GetInt64());
    }

    [Fact]
    public async Task FixR1_F6_disabled_game_tile_still_lists_rated_player_counts()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        await S3TestHelpers.RegisterConsole(factory.CreateClientWithKey(), op, "PS5-F6", 2);
        var create = await mgr.PostAsJsonAsync("/api/operator/game-titles", new { name = "FC27", enabled = true, rates = Fc27Rates });
        create.EnsureSuccessStatusCode();
        var gameId = (await create.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetInt32();
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(factory.CreateClientWithKey(), "PS5-F6", 2);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ver = await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
            await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/game", new { gameTitleId = gameId, expectedVersion = ver, commandId = Guid.NewGuid() });
        }
        await mgr.PutAsJsonAsync($"/api/operator/game-titles/{gameId}", new { name = "FC27", enabled = false, rates = Fc27Rates });
        var tiles = await op.GetFromJsonAsync<JsonElement>("/api/operator/grid");
        var tile = tiles.EnumerateArray().First(t =>
            t.TryGetProperty("name", out var stationName) && stationName.GetString() == "PS5-F6");
        var counts = tile.GetProperty("currentGameRatedPlayerCounts").EnumerateArray().Select(e => e.GetInt32()).OrderBy(x => x).ToArray();
        Assert.Equal([1, 2], counts);
    }

    [Fact]
    public async Task FixR1_F7_game_disabled_returns_persian_message()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        await S3TestHelpers.RegisterConsole(factory.CreateClientWithKey(), op, "PS5-F7", 1);
        var create = await mgr.PostAsJsonAsync("/api/operator/game-titles", new { name = "FC27", enabled = true, rates = Fc27Rates });
        create.EnsureSuccessStatusCode();
        var gameId = (await create.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetInt32();
        await mgr.PutAsJsonAsync($"/api/operator/game-titles/{gameId}", new { name = "FC27", enabled = false, rates = Fc27Rates });
        var start = await op.PostAsJsonAsync("/api/operator/stations/PS5-F7/start", new { gameTitleId = gameId });
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        var json = await start.Content.ReadFromJsonAsync<JsonElement>();
        var msg = json.GetProperty("message").GetString();
        Assert.Contains("غیرفعال", msg, StringComparison.Ordinal);
        Assert.Contains("گزینه", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("Game is disabled", msg, StringComparison.Ordinal);
    }

    private async Task<long> BookedSecondsAsync(Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId).Select(s => s.PrepaidBookedActiveSeconds ?? 0).SingleAsync();
    }

    private async Task<uint> RowVersionAsync(Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
    }

    private async Task<int> SeedFc27Async(HttpClient mgr)
    {
        var res = await mgr.PostAsJsonAsync(
            "/api/operator/game-titles",
            new { name = "FC27", enabled = true, rates = Fc27Rates });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task FixR1_F8_game_change_after_pause_rebooks_2760_active_seconds()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-P1", 1);
        var gameId = await SeedFc27Async(mgr);
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-P1", 1);
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/prepaid/amount", new { amountToman = 100_000L }))
            .EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 14, 10);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/pause", null)).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 14, 20);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/resume", null)).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 14, 30);
        var ver = await RowVersionAsync(sessionId);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = gameId, expectedVersion = ver, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        Assert.Equal(2_760L, await BookedSecondsAsync(sessionId));
    }

    [Fact]
    public async Task FixR1_F8_D6_prepaid_after_pause_grants_2760_active_seconds()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-D6", 1);
        factory.Clock.SetTehran(2026, 8, 8, 7, 50);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-D6", 1);
        factory.Clock.SetTehran(2026, 8, 8, 8, 0);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/pause", null)).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 8, 20);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/resume", null)).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 8, 25);
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/prepaid/amount", new { amountToman = 100_000L }))
            .EnsureSuccessStatusCode();
        Assert.Equal(2_760L, await BookedSecondsAsync(sessionId));
    }

    [Fact]
    public async Task FixR1_F8_M6c_transfer_after_pause_rebooks_2880_active_seconds()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-TR1", 1);
        await S3TestHelpers.RegisterConsole(api, op, "PS5-TR2", 1);
        var gameId = await SeedFc27Async(mgr);
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var start = await op.PostAsJsonAsync("/api/operator/stations/PS5-TR1/start", new { gameTitleId = gameId });
        var sessionId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/prepaid/amount", new { amountToman = 100_000L }))
            .EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 14, 10);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/pause", null)).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 14, 20);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/resume", null)).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 14, 30);
        var ver = await RowVersionAsync(sessionId);
        (await S3TestHelpers.TransferAsync(op, sessionId, "PS5-TR2", expectedVersion: ver)).EnsureSuccessStatusCode();
        Assert.Equal(2_880L, await BookedSecondsAsync(sessionId));
    }

    [Fact]
    public async Task FixR1_F8_M7_D4_recompute_grace_minute_20_minutes_booked()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-M7", 1);
        var gameId = await SeedFc27Async(mgr);
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-M7", 1);
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/prepaid/amount", new { amountToman = 37_000L }))
            .EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 14, 19);
        var ver = await RowVersionAsync(sessionId);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = gameId, expectedVersion = ver, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        Assert.Equal(20 * 60L, await BookedSecondsAsync(sessionId));
    }
}