using System.Text.Json;
using GameClub.Kiosk.Core.Catalog;
using GameClub.Kiosk.Core.Lease;
using GameClub.Kiosk.Core.Offline;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Kiosk.Core.Security;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskR6CoreTests
{
    [Fact]
    public void guard_identity_fail_closed_without_sid()
    {
        Assert.False(KioskGuardIdentityCore.TryResolveSid(null, "CheckpointKiosk", out _, out var error));
        Assert.Equal("kiosk_sid_missing", error);
    }

    [Fact]
    public void registry_policy_tamper_detects_non_taskmgr_drift()
    {
        var actual = new Dictionary<string, int?>
        {
            ["DisableTaskMgr"] = 1,
            ["DisableLockWorkstation"] = 1,
            ["DisableChangePassword"] = 1,
            ["NoDispCPL"] = 0,
            ["NoCloseExplorer"] = 1,
        };
        Assert.True(KioskRegistryPolicyTemplate.IsTampered(actual));
    }

    [Fact]
    public void orchestrator_catalog_filter_reduces_launch_list_by_station_group()
    {
        var orchestrator = new KioskShellOrchestrator { StationGroupId = "pc-normal" };
        var items = new[]
        {
            new GameCatalogItem(1, "A", 1, "a.exe", null, null, 1, true, [], [], null),
            new GameCatalogItem(2, "B", 1, "b.exe", null, null, 2, true, [], ["pc-vip"], null),
        };
        var visible = orchestrator.FilterVisibleGames(items).Select(i => i.Id).ToArray();
        Assert.Equal([1], visible);
    }

    [Fact]
    public void orchestrator_invalid_lease_transitions_to_lease_expired()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var orchestrator = new KioskShellOrchestrator();
        orchestrator.ConfigureLeaseVerifier(rsa.ExportSubjectPublicKeyInfoPem());
        var ack = new KioskHeartbeatAck
        {
            SessionStatus = "Open",
            Lease = new SignedSessionLease
            {
                Payload = new SessionLeasePayload
                {
                    SessionId = Guid.NewGuid(),
                    AuthorizedUntilUtc = DateTime.UtcNow.AddMinutes(-1),
                    IssuedAtUtc = DateTime.UtcNow,
                },
                SignatureBase64 = Convert.ToBase64String([1, 2, 3]),
            },
        };
        var phase = orchestrator.OnHeartbeatAck(ack, Guid.NewGuid(), 1);
        Assert.Equal(KioskShellPhase.LeaseExpired, phase);
    }

    [Fact]
    public async Task orchestrator_offline_queue_enqueue_then_drain_ack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"r6-queue-{Guid.NewGuid():N}.db");
        try
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var verifier = new LeaseVerifier(rsa.ExportSubjectPublicKeyInfoPem());
            var payload = new SessionLeasePayload
            {
                SessionId = Guid.NewGuid(),
                StationId = "PC-1",
                AuthorizedUntilUtc = DateTime.UtcNow.AddHours(1),
                IssuedAtUtc = DateTime.UtcNow,
            };
            var data = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            var lease = new SignedSessionLease
            {
                Payload = payload,
                SignatureBase64 = Convert.ToBase64String(rsa.SignData(data, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1)),
            };
            Assert.True(verifier.TryVerify(lease));

            var queue = new SqliteOfflineEventQueue(path);
            var orchestrator = new KioskShellOrchestrator();
            orchestrator.ConfigureLeaseVerifier(rsa.ExportSubjectPublicKeyInfoPem());
            orchestrator.BindOfflineQueue(queue);
            orchestrator.OnHeartbeatAck(new KioskHeartbeatAck { SessionStatus = "Open", Lease = lease }, payload.SessionId, 60_000);
            var pending = await queue.PendingAsync();
            Assert.NotEmpty(pending);
            var acked = await orchestrator.DrainOfflineQueueAsync(_ => Task.FromResult<IReadOnlyList<Guid>>(pending.Select(p => p.EventId).ToList()), default);
            Assert.Equal(pending.Count, acked);
            Assert.Empty(await queue.PendingAsync());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
