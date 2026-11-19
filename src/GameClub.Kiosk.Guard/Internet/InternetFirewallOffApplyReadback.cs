using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GameClub.Kiosk.Core.Guard;

namespace GameClub.Kiosk.Guard.Internet;

internal sealed class InternetFirewallOffApplyReadback
{
    private const string GroupName = "Checkpoint Internet Off";

    public InternetFirewallOffReadbackModel Model { get; set; }
    public IReadOnlyList<string> ActiveProfileOutbound { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> LocalhostProfileOutbound { get; set; } = Array.Empty<string>();

    public string? ClassifyOff(bool requireVerifiedStateFile) => Model.ClassifyOff(requireVerifiedStateFile);

    public void WriteL17DiagnosticLines(Action<string> writeLine)
    {
        writeLine(
            "L17_OFF_APPLY_DIAG active_profiles_out="
            + string.Join(",", ActiveProfileOutbound)
            + " localhost_profiles_out="
            + string.Join(",", LocalhostProfileOutbound)
            + " active_group_rule_count="
            + Model.ActiveStoreGuardRuleCount
            + " localhost_group_rule_count="
            + Model.LocalhostStoreGuardRuleCount
            + " checkpoint_server_reachable="
            + Model.CheckpointServerReachable
            + " state_present="
            + Model.StateFilePresent);
    }

    public static InternetFirewallOffApplyReadback Capture(InternetGuardConfig config, int? captureTimeoutMs = null)
    {
        var script = $@"
$group = '{GroupName}'
$activeOut = @()
$localOut = @()
foreach ($prof in @('Domain','Private','Public')) {{
  $a = Get-NetFirewallProfile -Name $prof -ErrorAction SilentlyContinue
  $activeOut += if ($a) {{ $a.DefaultOutboundAction.ToString() }} else {{ 'missing' }}
  $l = Get-NetFirewallProfile -PolicyStore localhost -Name $prof -ErrorAction SilentlyContinue
  $localOut += if ($l) {{ $l.DefaultOutboundAction.ToString() }} else {{ 'missing' }}
}}
$activeRules = @(Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue)
$localRules = @(Get-NetFirewallRule -PolicyStore localhost -Group $group -ErrorAction SilentlyContinue)
@{{
  activeOut = $activeOut
  localOut = $localOut
  activeRuleCount = $activeRules.Count
  localRuleCount = $localRules.Count
}} | ConvertTo-Json -Compress
";
        var budget = captureTimeoutMs ?? InternetGuardReconcilePolicy.ResolveApplyReadbackBudgetMs();
        var json = RunPowerShell(script, budget);
        List<string> active = [];
        List<string> local = [];
        var activeCount = 0;
        var localCount = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("activeOut", out var a) && a.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in a.EnumerateArray())
                {
                    active.Add(el.GetString() ?? "");
                }
            }

            if (root.TryGetProperty("localOut", out var l) && l.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in l.EnumerateArray())
                {
                    local.Add(el.GetString() ?? "");
                }
            }

            if (root.TryGetProperty("activeRuleCount", out var arc))
            {
                activeCount = arc.GetInt32();
            }

            if (root.TryGetProperty("localRuleCount", out var lrc))
            {
                localCount = lrc.GetInt32();
            }
        }
        catch
        {
            /* leave defaults; classifier will fail */
        }

        var reachable = ProbeServer(config);
        var model = new InternetFirewallOffReadbackModel
        {
            AllActiveProfilesBlock = active.Count == 3 && active.All(v => string.Equals(v, "Block", StringComparison.OrdinalIgnoreCase)),
            AnyActiveProfileBlock = active.Any(v => string.Equals(v, "Block", StringComparison.OrdinalIgnoreCase)),
            AllLocalhostProfilesBlock = local.Count == 3 && local.All(v => string.Equals(v, "Block", StringComparison.OrdinalIgnoreCase)),
            ActiveStoreGuardRuleCount = activeCount,
            LocalhostStoreGuardRuleCount = localCount,
            CheckpointServerReachable = reachable,
            StateFilePresent = File.Exists(InternetGuardPaths.StatePath),
        };
        return new InternetFirewallOffApplyReadback
        {
            Model = model,
            ActiveProfileOutbound = active,
            LocalhostProfileOutbound = local
        };
    }

    public static void WriteL17Diagnostics(InternetGuardConfig config, Action<string> writeLine)
        => Capture(config).WriteL17DiagnosticLines(writeLine);

    public static InternetFirewallOffApplyReadback ParseFromPowerShellJson(string json, InternetGuardConfig config)
    {
        List<string> active = [];
        List<string> local = [];
        var activeCount = 0;
        var localCount = 0;
        using (var doc = JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("activeOut", out var a) && a.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in a.EnumerateArray())
                {
                    active.Add(el.GetString() ?? "");
                }
            }

            if (root.TryGetProperty("localOut", out var l) && l.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in l.EnumerateArray())
                {
                    local.Add(el.GetString() ?? "");
                }
            }

            if (root.TryGetProperty("activeRuleCount", out var arc))
            {
                activeCount = arc.GetInt32();
            }

            if (root.TryGetProperty("localRuleCount", out var lrc))
            {
                localCount = lrc.GetInt32();
            }
        }

        var model = new InternetFirewallOffReadbackModel
        {
            AllActiveProfilesBlock = active.Count == 3 && active.All(v => string.Equals(v, "Block", StringComparison.OrdinalIgnoreCase)),
            AnyActiveProfileBlock = active.Any(v => string.Equals(v, "Block", StringComparison.OrdinalIgnoreCase)),
            AllLocalhostProfilesBlock = local.Count == 3 && local.All(v => string.Equals(v, "Block", StringComparison.OrdinalIgnoreCase)),
            ActiveStoreGuardRuleCount = activeCount,
            LocalhostStoreGuardRuleCount = localCount,
            CheckpointServerReachable = ProbeServer(config),
            StateFilePresent = File.Exists(InternetGuardPaths.StatePath),
        };
        return new InternetFirewallOffApplyReadback
        {
            Model = model,
            ActiveProfileOutbound = active,
            LocalhostProfileOutbound = local,
        };
    }

    private static bool ProbeServer(InternetGuardConfig config)
    {
        if (config.ServerIps.Length == 0 || config.ServerPort <= 0)
        {
            return false;
        }

        foreach (var ip in config.ServerIps)
        {
            try
            {
                using var tcp = new TcpClient();
                var task = tcp.ConnectAsync(ip, config.ServerPort);
                if (task.Wait(2_000) && tcp.Connected)
                {
                    return true;
                }
            }
            catch
            {
                /* try next */
            }
        }

        return false;
    }

    private static string RunPowerShell(string script, int timeoutMs, string stepName = "readback_capture")
        => InternetPowerShellRunner.Run(script, timeoutMs, stepName);
}