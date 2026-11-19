#if NET48
#nullable disable
using System;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameClub.Kiosk.Core.Connection;
using GameClub.Kiosk.Core.Guard;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Guard.Internet;
using Microsoft.AspNetCore.SignalR.Client;
using Npgsql;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

/// <summary>L17 probe — signed Guard pipe apply (option A); L17 assertion strings unchanged.</summary>
internal static class L17RealInternetOffProbe
{
    internal static async Task RunAsync(
        InternetGuardConfig config,
        Guid deviceId,
        string deviceKey,
        Uri hubUri,
        string stationId,
        CancellationToken ct)
    {
        var applier = new InternetFirewallApplier();
        try
        {
            await SeedStationInternetDesiredOffAsync(stationId, ct).ConfigureAwait(false);
            var command = await FetchSignedInternetOffAsync(hubUri, deviceId, deviceKey, ct).ConfigureAwait(false);
            var client = new GuardInternetClient();
            var raw = await client.ApplyAsync(command, ct).ConfigureAwait(false);
            var applyResult = ParsePipeApplyResult(raw);
            InternetFirewallOffDiagnostics.WriteL17OffApplyDiagnostics(config, Console.WriteLine);
            var ruleCount = ReadActiveStoreGuardRuleCount();
            var localhostRuleCount = ReadLocalhostStoreGuardRuleCount();
            var blocked = ProbeEgress("1.1.1.1", 443);
            var statePresent = File.Exists(InternetGuardPaths.StatePath);
            Console.WriteLine(
                $"L17_GUARD_OFF_PROBE apply_result={applyResult} blocked={blocked} guard_rule_count={ruleCount} "
                + $"state_present={statePresent} state_path={InternetGuardPaths.StatePath}");
            Assert.True(blocked, "real internet-off probe expected public egress blocked");
            Assert.True(ruleCount >= 1, "real internet-off probe expected guard_rule_count >= 1");
            Assert.True(statePresent, "real internet-off probe expected guard state file present");
            Assert.Equal(0, localhostRuleCount);
        }
        finally
        {
            applier.Apply(config, on: true);
        }
    }

    private static string ParsePipeApplyResult(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "apply_failed:empty_pipe_response";
        }

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("error", out var err))
        {
            return err.GetString() ?? "apply_failed";
        }

        if (doc.RootElement.TryGetProperty("result", out var result))
        {
            return result.GetString() ?? "applied";
        }

        return "applied";
    }

    private static async Task<SignedInternetCommand> FetchSignedInternetOffAsync(
        Uri hubUri,
        Guid deviceId,
        string deviceKey,
        CancellationToken ct)
    {
        var hub = KioskConnectionFactory.Create(hubUri, deviceId.ToString(), deviceKey, legacyWindows: true);
        await hub.StartAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var ack = await hub.InvokeAsync<KioskHeartbeatAck>(
                    "Heartbeat",
                    new KioskHeartbeatRequest { SessionId = null, ClientMonotonicMs = attempt * 1_000 },
                    ct).ConfigureAwait(false);
                if (ack.Internet is not null && ack.Internet.Payload is not null && !ack.Internet.Payload.On)
                {
                    return ack.Internet;
                }

                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await hub.DisposeAsync().ConfigureAwait(false);
        }

        throw new InvalidOperationException("L17 probe could not obtain signed internet-off command from server heartbeat.");
    }

    private static async Task SeedStationInternetDesiredOffAsync(string stationId, CancellationToken ct)
    {
        var connString = Environment.GetEnvironmentVariable("GAMECLUB_TEST_DATABASE")
            ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE");
        if (string.IsNullOrWhiteSpace(connString))
        {
            throw new InvalidOperationException("GAMECLUB_TEST_DATABASE must be set for L17 probe.");
        }

        using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var epoch = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using var cmd = new NpgsqlCommand(
            """
            INSERT INTO station_internet_state (
              "StationId", "Epoch", "DesiredOn", "DesiredVersion", "DesiredAtUtc", "DesiredReason")
            VALUES (@sid, @epoch, false, 1, @now, 'l17_probe')
            ON CONFLICT ("StationId") DO UPDATE SET
              "DesiredOn" = false,
              "DesiredVersion" = station_internet_state."DesiredVersion" + 1,
              "DesiredAtUtc" = @now,
              "DesiredReason" = 'l17_probe',
              "Epoch" = @epoch
            """,
            conn);
        cmd.Parameters.AddWithValue("sid", stationId);
        cmd.Parameters.AddWithValue("epoch", epoch);
        cmd.Parameters.AddWithValue("now", now);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static int ReadActiveStoreGuardRuleCount()
    {
        try
        {
            var script =
                "(Get-NetFirewallRule -Group 'Checkpoint Internet Off' -ErrorAction SilentlyContinue | Measure-Object).Count";
            var psi = new System.Diagnostics.ProcessStartInfo("powershell", "-NoProfile -Command " + script)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            var text = p!.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10_000);
            return int.TryParse(text, out var count) ? count : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static int ReadLocalhostStoreGuardRuleCount()
    {
        try
        {
            var script =
                "(Get-NetFirewallRule -PolicyStore localhost -Group 'Checkpoint Internet Off' -ErrorAction SilentlyContinue | Measure-Object).Count";
            var psi = new System.Diagnostics.ProcessStartInfo("powershell", "-NoProfile -Command " + script)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            var text = p!.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10_000);
            return int.TryParse(text, out var count) ? count : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static bool ProbeEgress(string host, int port)
    {
        try
        {
            using var tcp = new TcpClient();
            var task = tcp.ConnectAsync(host, port);
            return !(task.Wait(2_000) && tcp.Connected);
        }
        catch
        {
            return true;
        }
    }
}
#endif