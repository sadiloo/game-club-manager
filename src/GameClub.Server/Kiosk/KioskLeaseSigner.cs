using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Server.Kiosk;

public interface IKioskLeaseSigner
{
    string PublicKeyPem { get; }
    SignedSessionLease Sign(SessionLeasePayload payload);
}

public sealed class KioskLeaseSigner : IKioskLeaseSigner, IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);

    public KioskLeaseSigner(IConfiguration config)
    {
        var pem = config["GAMECLUB_KIOSK_LEASE_SIGNING_KEY_PEM"];
        if (!string.IsNullOrWhiteSpace(pem))
        {
            _rsa.ImportFromPem(pem);
        }
    }

    public string PublicKeyPem => _rsa.ExportSubjectPublicKeyInfoPem();

    public SignedSessionLease Sign(SessionLeasePayload payload)
    {
        var canonical = JsonSerializer.Serialize(payload);
        var data = Encoding.UTF8.GetBytes(canonical);
        var sig = _rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return new SignedSessionLease { Payload = payload, SignatureBase64 = Convert.ToBase64String(sig) };
    }

    public void Dispose() => _rsa.Dispose();
}
