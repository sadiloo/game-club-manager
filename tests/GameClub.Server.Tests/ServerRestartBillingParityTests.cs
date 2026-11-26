using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class ServerRestartBillingParityTests
{
    [Fact]
    public async Task short_restart_preserves_console_and_pc_session_charges()
    {
        var control = await RunTimelineAsync(restartHost: false, tag: "ctl");
        var restarted = await RunTimelineAsync(restartHost: true, tag: "rst");

        AssertBillParity(control.ConsoleBill, restarted.ConsoleBill);
        AssertBillParity(control.PcBill, restarted.PcBill);
    }

    private static void AssertBillParity(JsonElement control, JsonElement restarted)
    {
        Assert.Equal(
            control.GetProperty("unroundedPayableToman").GetInt64(),
            restarted.GetProperty("unroundedPayableToman").GetInt64());
        Assert.Equal(
            control.GetProperty("collectibleToman").GetInt64(),
            restarted.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(
            control.GetProperty("roundingToman").GetInt64(),
            restarted.GetProperty("roundingToman").GetInt64());
        Assert.Equal(
            control.GetProperty("timeGrossToman").GetInt64(),
            restarted.GetProperty("timeGrossToman").GetInt64());
    }

    private static async Task<(JsonElement ConsoleBill, JsonElement PcBill)> RunTimelineAsync(bool restartHost, string tag)
    {
        var ps5 = $"PS5-B8-{tag}";
        var pc = $"PC-B8-{tag}";
        var start = new DateTimeOffset(2026, 6, 3, 14, 0, 0, TimeSpan.FromHours(3.5));
        var downtime = start.AddMinutes(55);
        var resume = new DateTimeOffset(2026, 6, 3, 15, 20, 0, TimeSpan.FromHours(3.5));
        var closeAt = resume.AddMinutes(5);

        Guid consoleId;
        Guid pcId;

        await using (var factory = new GameClubWebApplicationFactory())
        {
            await S3TestHelpers.ResetAsync(factory);
            var api = factory.CreateClientWithKey();
            var op = await factory.CreateOperatorClientAsync();
            await S3TestHelpers.RegisterConsole(api, op, ps5, 1);
            await S3TestHelpers.RegisterPc(api, pc, PilotSeed.PcNormalGroupId);

            factory.Clock.SetUtc(start);
            consoleId = await S3TestHelpers.StartSession(api, ps5, 1);
            pcId = await S3TestHelpers.StartSession(api, pc, 1);

            factory.Clock.SetUtc(start.AddMinutes(50));
            await S3TestHelpers.CheckpointAsync(api, consoleId, start.AddMinutes(50));
            await S3TestHelpers.CheckpointAsync(api, pcId, start.AddMinutes(50));

            factory.Clock.SetUtc(downtime);
            await S3TestHelpers.CheckpointAsync(api, consoleId, downtime);
            await S3TestHelpers.CheckpointAsync(api, pcId, downtime);

            if (!restartHost)
            {
                return await FinishAndBillAsync(factory, api, op, consoleId, pcId, resume, closeAt);
            }
        }

        await using (var factory = new GameClubWebApplicationFactory { PreserveDatabaseOnBootstrap = true })
        {
            factory.Clock.SetUtc(downtime.AddSeconds(30));
            factory.Clock.SetUtc(downtime.AddMinutes(5));
            factory.Clock.SetUtc(resume);
            var api = factory.CreateClientWithKey();
            var op = await factory.CreateOperatorClientAsync();
            return await FinishAndBillAsync(factory, api, op, consoleId, pcId, resume, closeAt);
        }
    }

    private static async Task<(JsonElement ConsoleBill, JsonElement PcBill)> FinishAndBillAsync(
        GameClubWebApplicationFactory factory,
        HttpClient api,
        HttpClient op,
        Guid consoleId,
        Guid pcId,
        DateTimeOffset resume,
        DateTimeOffset closeAt)
    {
        factory.Clock.SetUtc(resume);
        await S3TestHelpers.CheckpointAsync(api, consoleId, resume);
        await S3TestHelpers.CheckpointAsync(api, pcId, resume);
        factory.Clock.SetUtc(closeAt);
        await S3TestHelpers.CheckpointAsync(api, consoleId, closeAt);
        await S3TestHelpers.CheckpointAsync(api, pcId, closeAt);
        (await api.PostAsync($"/api/sessions/{consoleId}/stop", null)).EnsureSuccessStatusCode();
        (await api.PostAsync($"/api/sessions/{pcId}/stop", null)).EnsureSuccessStatusCode();
        var consoleBill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{consoleId}/bill");
        var pcBill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{pcId}/bill");
        return (consoleBill, pcBill);
    }
}