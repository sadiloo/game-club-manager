using System.Text.Json;
using GameClub.Kiosk.Core.Catalog;
using GameClub.Kiosk.Core.Lease;
using GameClub.Kiosk.Core.Offline;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Kiosk.Core.Security;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskFixR8CoreRuntimeTests
{
    [Fact]
    public async Task transport_down_raises_callback_and_tick_lease_queues_extra_powered_segment()
    {
        var path = Path.Combine(Path.GetTempPath(), $"r8-queue-{Guid.NewGuid():N}.db");
        var queue = new SqliteOfflineEventQueue(path);
        try
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var orchestrator = new KioskShellOrchestrator();
            orchestrator.ConfigureLeaseVerifier(rsa.ExportSubjectPublicKeyInfoPem());
            orchestrator.BindOfflineQueue(queue);
            var sessionId = Guid.NewGuid();
            var lease = SignLease(rsa, sessionId, DateTime.UtcNow.AddMinutes(10));
            orchestrator.OnHeartbeatAck(new KioskHeartbeatAck { SessionStatus = "Open", Lease = lease }, sessionId, 1_000);

            var runtime = new KioskRuntime(() => new HttpClient());
            var queuePath = Path.Combine(Path.GetTempPath(), $"r8-runtime-{Guid.NewGuid():N}.db");
            var failures = 0;
            runtime.HeartbeatTransportFailed += () => failures++;
            runtime.Configure(new Uri("http://127.0.0.1:59999/hubs/kiosk"), Guid.NewGuid(), "device-key", "PC-1", queuePath, legacyWindows: true);
            runtime.BindSession(sessionId);
            runtime.SignalTransportDownIfNeeded();
            Assert.Equal(1, failures);

            orchestrator.TickLease(sessionId, 40_000, DateTime.UtcNow, transportDown: true);
            var pending = await queue.PendingAsync();
            Assert.True(pending.Count >= 2);
            Assert.All(pending, p => Assert.Equal("offline_play_segment", p.Type));
        }
        finally
        {
            queue.Close();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void allowlist_rejects_substring_tokens_and_keeps_exact_matches()
    {
        var enforcer = new ProcessPolicyEnforcer();
        Assert.False(enforcer.IsProcessAllowed("r7game-malicious", ["r7game"]));
        Assert.True(enforcer.IsProcessAllowed("r7game", ["r7game"]));
        Assert.False(enforcer.IsProcessAllowed("cmd", ["m"]));
        Assert.False(enforcer.IsProcessAllowed("unlisted-game-malware", ["game"]));
        Assert.True(enforcer.IsProcessAllowed("steamwebhelper", ["r7game"]));
    }

    [Fact]
    public async Task catalog_refresh_loads_sqlite_on_http_failure_and_empty_delta()
    {
        var db = Path.Combine(Path.GetTempPath(), $"r8-catalog-{Guid.NewGuid():N}.db");
        var store = new SqliteCatalogCache(db);
        var seeded = new GameCatalogItem(1, "Disk Game", 1, "C:\\game.exe", null, null, 0, true, [], [], null);
        await store.SaveAsync([seeded], 3);

        var result = await CatalogRefreshMerger.RefreshAsync(
            _ => Task.FromResult(new CatalogHttpOutcome(false, 0, [])),
            store,
            [],
            0);
        Assert.Single(result.Items);
        Assert.Equal("Disk Game", result.Items[0].Name);

        var emptyDelta = await CatalogRefreshMerger.RefreshAsync(
            _ => Task.FromResult(new CatalogHttpOutcome(true, 3, [])),
            store,
            [],
            3);
        Assert.Single(emptyDelta.Items);
    }

    [Fact]
    public async Task catalog_refresh_loads_disk_when_fetch_throws()
    {
        var db = Path.Combine(Path.GetTempPath(), $"r8-catalog-throw-{Guid.NewGuid():N}.db");
        var store = new SqliteCatalogCache(db);
        await store.SaveAsync([new GameCatalogItem(2, "Cached", 1, "game.exe", null, null, 0, true, [], [], null)], 5);

        var result = await CatalogRefreshMerger.RefreshAsync(
            _ => throw new InvalidOperationException("network down"),
            store,
            [],
            0);
        Assert.Single(result.Items);
        Assert.Equal("Cached", result.Items[0].Name);
    }

    [Fact]
    public void station_profile_pairing_path_applies_test_mode_without_login()
    {
        var orchestrator = new KioskShellOrchestrator();
        Assert.True(orchestrator.FinalMode);
        orchestrator.ApplyStationConfig(isTestMode: true);
        Assert.False(orchestrator.FinalMode);
        orchestrator.ApplyStationConfig(isTestMode: false);
        Assert.True(orchestrator.FinalMode);
    }

    [Fact]
    public void guard_nologoff_is_explorer_policy_not_system_policy()
    {
        Assert.DoesNotContain("NoLogoff", KioskRegistryPolicyTemplate.SystemPolicies.Keys);
        Assert.Contains("NoLogoff", KioskRegistryPolicyTemplate.ExplorerPolicies.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\DisallowRun",
            KioskRegistryPolicyTemplate.DisallowRunRelativePath);
    }

    [Fact]
    public void guard_disallow_run_tamper_detects_missing_cmd_entry()
    {
        var system = KioskRegistryPolicyTemplate.SystemPolicies.ToDictionary(
            kvp => kvp.Key,
            kvp => (int?)kvp.Value,
            StringComparer.OrdinalIgnoreCase);
        var explorer = KioskRegistryPolicyTemplate.ExplorerPolicies.ToDictionary(
            kvp => kvp.Key,
            kvp => (int?)kvp.Value,
            StringComparer.OrdinalIgnoreCase);
        var broken = KioskRegistryPolicyTemplate.ResolveDisallowRunEntries().Skip(1).ToList();
        Assert.True(KioskRegistryPolicyTemplate.IsTampered(system, explorer, broken));
    }

    private static SignedSessionLease SignLease(System.Security.Cryptography.RSA rsa, Guid sessionId, DateTime untilUtc)
    {
        var payload = new SessionLeasePayload
        {
            SessionId = sessionId,
            StationId = "PC-1",
            AuthorizedUntilUtc = untilUtc,
            IssuedAtUtc = DateTime.UtcNow,
        };
        var data = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        return new SignedSessionLease
        {
            Payload = payload,
            SignatureBase64 = Convert.ToBase64String(rsa.SignData(data, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1)),
        };
    }
}
