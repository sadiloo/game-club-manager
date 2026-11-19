using System.Collections.Generic;
using System.Text.Json;

namespace GameClub.Kiosk.Core.Guard;

/// <summary>Parsed firewall re-read snapshot for classifier wiring (PR40-R15).</summary>
public readonly struct InternetFirewallOffReadbackModel
{
    public bool AllActiveProfilesBlock { get; init; }
    public bool AllLocalhostProfilesBlock { get; init; }
    public int ActiveStoreGuardRuleCount { get; init; }
    public int LocalhostStoreGuardRuleCount { get; init; }
    public bool CheckpointServerReachable { get; init; }
    public bool StateFilePresent { get; init; }

    public string? ClassifyOff(bool requireVerifiedStateFile)
        => InternetFirewallOffVerification.TryGetApplyFailedReason(
            AllActiveProfilesBlock,
            AllLocalhostProfilesBlock,
            ActiveStoreGuardRuleCount,
            CheckpointServerReachable,
            StateFilePresent,
            requestedOn: false,
            requireVerifiedStateFile);

    public static bool TryParseDiagnosticJson(
        string json,
        out InternetFirewallOffReadbackModel model,
        out string[] activeOut,
        out string[] localOut)
    {
        model = default;
        activeOut = [];
        localOut = [];
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            activeOut = ReadStringArray(root, "activeOut");
            localOut = ReadStringArray(root, "localOut");
            var activeRules = root.TryGetProperty("activeRuleCount", out var ar) ? ar.GetInt32() : 0;
            var localRules = root.TryGetProperty("localRuleCount", out var lr) ? lr.GetInt32() : 0;
            model = new InternetFirewallOffReadbackModel
            {
                AllActiveProfilesBlock = activeOut.Length == 3 && AllBlock(activeOut),
                AllLocalhostProfilesBlock = localOut.Length == 3 && AllBlock(localOut),
                ActiveStoreGuardRuleCount = activeRules,
                LocalhostStoreGuardRuleCount = localRules,
                CheckpointServerReachable = root.TryGetProperty("checkpointServerReachable", out var reach) && reach.GetBoolean(),
                StateFilePresent = root.TryGetProperty("stateFilePresent", out var st) && st.GetBoolean(),
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool AllBlock(string[] values)
    {
        foreach (var v in values)
        {
            if (!string.Equals(v, "Block", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string[] ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string>();
        foreach (var el in arr.EnumerateArray())
        {
            list.Add(el.GetString() ?? "");
        }

        return list.ToArray();
    }
}