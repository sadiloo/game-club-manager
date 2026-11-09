using System.Text.Json;
using GameClub.Kiosk.Core.Catalog;
using GameClub.Kiosk.Core.Lease;
using GameClub.Kiosk.Core.Offline;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Kiosk.Core.Security;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskFixR7CoreRuntimeTests
{
    [Fact]
    public void orchestrator_lease_expires_without_ack()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var orchestrator = new KioskShellOrchestrator();
        orchestrator.ConfigureLeaseVerifier(rsa.ExportSubjectPublicKeyInfoPem());
        var sessionId = Guid.NewGuid();
        var lease = SignLease(rsa, sessionId, DateTime.UtcNow.AddMinutes(5));
        orchestrator.OnHeartbeatAck(new KioskHeartbeatAck { SessionStatus = "Open", Lease = lease }, sessionId, 1_000);
        Assert.Equal(KioskShellPhase.Playing, orchestrator.Phase);
        var phase = orchestrator.TickLease(sessionId, 2_000, DateTime.UtcNow.AddMinutes(6));
        Assert.Equal(KioskShellPhase.LeaseExpired, phase);
    }

    [Fact]
    public void orchestrator_rejects_wrong_session_id_on_ack()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var orchestrator = new KioskShellOrchestrator();
        orchestrator.ConfigureLeaseVerifier(rsa.ExportSubjectPublicKeyInfoPem());
        var bound = Guid.NewGuid();
        var other = Guid.NewGuid();
        var lease = SignLease(rsa, other, DateTime.UtcNow.AddMinutes(5));
        var phase = orchestrator.OnHeartbeatAck(new KioskHeartbeatAck { SessionStatus = "Open", Lease = lease }, bound, 1_000);
        Assert.Equal(KioskShellPhase.Locked, phase);
    }

    [Fact]
    public async Task orchestrator_disconnect_appends_powered_segment()
    {
        var path = Path.Combine(Path.GetTempPath(), $"r7-queue-{Guid.NewGuid():N}.db");
        var queue = new SqliteOfflineEventQueue(path);
        try
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var orchestrator = new KioskShellOrchestrator();
            orchestrator.BindOfflineQueue(queue);
            var sessionId = Guid.NewGuid();
            orchestrator.EnqueuePoweredSegmentOnDisconnect(sessionId, 30_000);
            var pending = await queue.PendingAsync();
            Assert.Single(pending);
            Assert.Equal("offline_play_segment", pending[0].Type);
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
    public void process_policy_closes_unlisted_executable()
    {
        var enforcer = new ProcessPolicyEnforcer();
        Assert.False(enforcer.IsProcessAllowed("notepad", ["game.exe"]));
        Assert.True(enforcer.IsProcessAllowed("steam", ["game.exe"]));
    }

    [Fact]
    public void registry_policy_template_includes_disallow_run_entries()
    {
        Assert.True(KioskRegistryPolicyTemplate.DefaultDisallowRunExecutables.Count >= 5);
        Assert.Contains("cmd.exe", KioskRegistryPolicyTemplate.ResolveDisallowRunEntries(), StringComparer.OrdinalIgnoreCase);
    }

  [Fact]
    public void registry_policy_tamper_detects_sign_out_policy_drift()
    {
        var actual = KioskRegistryPolicyTemplate.SystemPolicies.ToDictionary(
            kvp => kvp.Key,
            kvp => (int?)kvp.Value,
            StringComparer.OrdinalIgnoreCase);
        actual["NoLogoff"] = 0;
        Assert.True(KioskRegistryPolicyTemplate.IsTampered(actual));
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
