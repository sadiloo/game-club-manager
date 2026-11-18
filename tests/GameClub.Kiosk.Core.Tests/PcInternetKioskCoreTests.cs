using System.Security.Cryptography;
using System.Text;
using GameClub.Kiosk.Core.Guard;
using GameClub.Kiosk.Core.Lease;
using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Core.Tests;

public sealed class PcInternetKioskCoreTests
{
    [Fact]
    public void PcInternet_verify_accepts_valid_signature()
    {
        using var rsa = RSA.Create(2048);
        var pub = rsa.ExportSubjectPublicKeyInfoPem();
        var payload = new InternetCommandPayload
        {
            StationId = "PC-1",
            Epoch = Guid.NewGuid(),
            Version = 1,
            On = false,
            IssuedAtUtc = DateTime.UtcNow,
        };
        var canonical = LeaseVerifier.CanonicalJson(payload);
        var sig = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var command = new SignedInternetCommand { Payload = payload, SignatureBase64 = sig };
        var verifier = new InternetCommandVerifier(pub);
        Assert.True(verifier.TryVerify(command, out var err));
        Assert.Equal("", err);
    }

    [Fact]
    public void PcInternet_verify_rejects_forged_signature()
    {
        using var rsa = RSA.Create(2048);
        var pub = rsa.ExportSubjectPublicKeyInfoPem();
        var payload = new InternetCommandPayload
        {
            StationId = "PC-1",
            Epoch = Guid.NewGuid(),
            Version = 1,
            On = false,
            IssuedAtUtc = DateTime.UtcNow,
        };
        var command = new SignedInternetCommand { Payload = payload, SignatureBase64 = Convert.ToBase64String(new byte[256]) };
        var verifier = new InternetCommandVerifier(pub);
        Assert.False(verifier.TryVerify(command, out var err));
        Assert.Equal("rejected_signature", err);
    }
}