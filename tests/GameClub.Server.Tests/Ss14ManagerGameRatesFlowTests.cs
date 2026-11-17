using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class Ss14ManagerGameRatesFlowTests(GameClubWebApplicationFactory factory)
{
    private static string AppJsPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameClub.Server", "wwwroot", "operator", "app.js"));

    [Fact]
    public void ss14_operator_app_block_has_no_window_dialogs()
    {
        var js = File.ReadAllText(AppJsPath());
        var start = js.IndexOf("// --- SS-14 game tariff ---", StringComparison.Ordinal);
        var end = js.IndexOf("// --- end SS-14 ---", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var block = js[start..end];
        Assert.DoesNotContain("window.prompt", block, StringComparison.Ordinal);
        Assert.DoesNotContain("window.alert", block, StringComparison.Ordinal);
        Assert.DoesNotContain("window.confirm", block, StringComparison.Ordinal);
        Assert.Contains("data-rate=\"hourly\"", block, StringComparison.Ordinal);
        foreach (var lit in Ss14ExtractJsStringLiterals(block))
        {
            Assert.DoesNotContain("/*", lit, StringComparison.Ordinal);
            Assert.DoesNotContain("متن موقت", lit, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> Ss14ExtractJsStringLiterals(string js)
    {
        var i = 0;
        while (i < js.Length)
        {
            var c = js[i];
            if (c is '"' or '\'' or '`')
            {
                var quote = c;
                var start = i + 1;
                i++;
                while (i < js.Length)
                {
                    if (js[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }
                    if (js[i] == quote)
                    {
                        yield return js[start..i];
                        i++;
                        break;
                    }
                    if (quote == '`' && js[i] == '$' && i + 1 < js.Length && js[i + 1] == '{')
                    {
                        i += 2;
                        var depth = 1;
                        while (i < js.Length && depth > 0)
                        {
                            if (js[i] == '{') depth++;
                            else if (js[i] == '}') depth--;
                            i++;
                        }
                        continue;
                    }
                    i++;
                }
                continue;
            }
            if (c == '/' && i + 1 < js.Length)
            {
                if (js[i + 1] == '/')
                {
                    i += 2;
                    while (i < js.Length && js[i] != '\n') i++;
                    continue;
                }
                if (js[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < js.Length && !(js[i] == '*' && js[i + 1] == '/')) i++;
                    i = Math.Min(i + 2, js.Length);
                    continue;
                }
            }
            i++;
        }
    }

    [Fact]
    public async Task Ss14Q14_OperatorForbiddenOnAllGameTitleWrites()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/operator/game-titles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/operator/game-titles/console-options")).StatusCode);
        var body = new
        {
            name = "X",
            enabled = true,
            rates = new[] { new { mode = "Offline", playerCount = (int?)1, hourlyToman = 150_000L } },
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/operator/game-titles", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PutAsJsonAsync("/api/operator/game-titles/1", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.DeleteAsync("/api/operator/game-titles/1")).StatusCode);
    }

    [Fact]
    public async Task Ss14Q14_ManagerCreatesRates_AuditSelectable_Disable_HidesRunningUnchanged()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        await S3TestHelpers.RegisterConsole(factory.CreateClientWithKey(), op, "PS5-MGR", 1);
        var create = await mgr.PostAsJsonAsync(
            "/api/operator/game-titles",
            new
            {
                name = "FC27",
                enabled = true,
                rates = new object[]
                {
                    new { mode = "Offline", playerCount = (int?)1, hourlyToman = 150_000L, nightHourlyToman = 180_000L },
                    new { mode = "Offline", playerCount = (int?)2, hourlyToman = 170_000L },
                },
            });
        create.EnsureSuccessStatusCode();
        var gameId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audits = await db.AuditLog.Where(a => a.Action == "game_rate.change").ToListAsync();
            Assert.Contains(audits, a => a.DetailsJson.Contains("newHourlyToman") && a.DetailsJson.Contains("150000"));
            Assert.Contains(audits, a => a.DetailsJson.Contains("oldHourlyToman") == false || a.DetailsJson.Contains("\"oldHourlyToman\":null"));
        }

        var sel1 = await op.GetFromJsonAsync<JsonElement>("/api/operator/game-titles/selectable?mode=Offline&playerCount=1");
        Assert.Contains(sel1.EnumerateArray(), e => e.GetProperty("id").GetInt32() == gameId);
        var sel2 = await op.GetFromJsonAsync<JsonElement>("/api/operator/game-titles/selectable?mode=Offline&playerCount=3");
        Assert.DoesNotContain(sel2.EnumerateArray(), e => e.GetProperty("id").GetInt32() == gameId);

        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(factory.CreateClientWithKey(), "PS5-MGR", 1);
        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var ver = await db2.Sessions.Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = gameId, expectedVersion = ver, commandId = Guid.NewGuid() });
        var openBefore = await db2.Segments.Where(s => s.SessionId == sessionId && s.EndUtc == null)
            .Select(s => s.GameHourlyRateToman).SingleAsync();

        (await mgr.PutAsJsonAsync(
            $"/api/operator/game-titles/{gameId}",
            new
            {
                name = "FC27",
                enabled = true,
                rates = new object[]
                {
                    new { mode = "Offline", playerCount = (int?)1, hourlyToman = 155_000L, nightHourlyToman = 180_000L },
                    new { mode = "Offline", playerCount = (int?)2, hourlyToman = 170_000L },
                },
            })).EnsureSuccessStatusCode();

        using (var scope3 = factory.Services.CreateScope())
        {
            var db = scope3.ServiceProvider.GetRequiredService<AppDbContext>();
            var change = await db.AuditLog.Where(a => a.Action == "game_rate.change")
                .OrderByDescending(a => a.Id).FirstAsync();
            Assert.Contains("155000", change.DetailsJson);
            Assert.Contains("150000", change.DetailsJson);
            var openAfter = await db.Segments.Where(s => s.SessionId == sessionId && s.EndUtc == null)
                .Select(s => s.GameHourlyRateToman).SingleAsync();
            Assert.Equal(openBefore, openAfter);
        }

        (await mgr.PutAsJsonAsync(
            $"/api/operator/game-titles/{gameId}",
            new
            {
                name = "FC27",
                enabled = false,
                rates = new object[]
                {
                    new { mode = "Offline", playerCount = (int?)1, hourlyToman = 155_000L },
                    new { mode = "Offline", playerCount = (int?)2, hourlyToman = 170_000L },
                },
            })).EnsureSuccessStatusCode();

        var selAfter = await op.GetFromJsonAsync<JsonElement>("/api/operator/game-titles/selectable?mode=Offline&playerCount=1");
        Assert.DoesNotContain(selAfter.EnumerateArray(), e => e.GetProperty("id").GetInt32() == gameId);

        using (var scope4 = factory.Services.CreateScope())
        {
            var db = scope4.ServiceProvider.GetRequiredService<AppDbContext>();
            var open = await db.Segments.Where(s => s.SessionId == sessionId && s.EndUtc == null).SingleAsync();
            Assert.Equal(gameId, open.GameTitleId);
            Assert.Equal(150_000L, open.GameHourlyRateToman);
        }
    }

    [Fact]
    public async Task Ss14C3_DuplicateCommandId_DifferentBody_KeepsFirstGame()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-C3", 1);
        var fcId = await mgr.PostAsJsonAsync(
            "/api/operator/game-titles",
            new
            {
                name = "FC27",
                enabled = true,
                rates = new[] { new { mode = "Offline", playerCount = (int?)1, hourlyToman = 150_000L } },
            });
        fcId.EnsureSuccessStatusCode();
        var fc = (await fcId.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var pesId = await mgr.PostAsJsonAsync(
            "/api/operator/game-titles",
            new
            {
                name = "PES",
                enabled = true,
                rates = new[] { new { mode = "Offline", playerCount = (int?)1, hourlyToman = 150_000L } },
            });
        pesId.EnsureSuccessStatusCode();
        var pes = (await pesId.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-C3", 1);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ver = await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
        var cmd = Guid.NewGuid();
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = fc, expectedVersion = ver, commandId = cmd })).EnsureSuccessStatusCode();
        var segCount = await db.Sessions.Include(s => s.Segments).Where(s => s.Id == sessionId)
            .Select(s => s.Segments.Count).SingleAsync();
        ver = await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = pes, expectedVersion = ver, commandId = cmd })).EnsureSuccessStatusCode();
        var session = await db.Sessions.Include(s => s.Segments).SingleAsync(s => s.Id == sessionId);
        Assert.Equal(fc, session.CurrentGameTitleId);
        Assert.Equal(segCount, session.Segments.Count);
    }
}