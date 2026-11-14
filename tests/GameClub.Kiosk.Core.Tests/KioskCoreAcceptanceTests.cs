using GameClub.Kiosk.Core.Catalog;
using GameClub.Kiosk.Core.Heartbeat;
using GameClub.Kiosk.Core.Lease;
using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskCoreAcceptanceTests
{
    [Fact]
    public void f5_launcher_ignores_catalog_age_field()
    {
        var items = new[]
        {
            new GameCatalogItem(1, "A", 1, "a.exe", null, null, 1, true, [], [], 18),
            new GameCatalogItem(2, "B", 1, "b.exe", null, null, 2, true, [], ["pc-vip"], null),
        };
        var filtered = GameCatalogFilter.ForStation(items, "pc-normal").Select(i => i.Id).ToArray();
        Assert.Equal([1], filtered);
    }

    [Fact]
    public void heartbeat_advances_checkpoint_each_minute()
    {
        var last = new DateTime(2026, 6, 3, 14, 0, 0, DateTimeKind.Utc);
        var serverNow = new DateTime(2026, 6, 3, 14, 2, 30, DateTimeKind.Utc);
        var minutes = CheckpointMinutePlanner.MinutesToAccept(last, serverNow);
        Assert.Equal(2, minutes.Count);
        Assert.Equal(new DateTime(2026, 6, 3, 14, 1, 0, DateTimeKind.Utc), minutes[0]);
        Assert.Equal(new DateTime(2026, 6, 3, 14, 2, 0, DateTimeKind.Utc), minutes[1]);
    }

    [Fact]
    public void lease_verifier_rejects_tampered_signature()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pem = rsa.ExportSubjectPublicKeyInfoPem();
        var verifier = new LeaseVerifier(pem);
        var lease = new SignedSessionLease
        {
            Payload = new SessionLeasePayload
            {
                LeaseId = Guid.NewGuid(),
                SessionId = Guid.NewGuid(),
                StationId = "PC-1",
                AuthorizedUntilUtc = DateTime.UtcNow.AddHours(1),
                MaxBillableCheckpointUtc = DateTime.UtcNow,
                IssuedAtUtc = DateTime.UtcNow,
            },
            SignatureBase64 = Convert.ToBase64String([1, 2, 3]),
        };
        Assert.False(verifier.TryVerify(lease));
    }
}
