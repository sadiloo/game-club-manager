using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Kiosk;

public interface IKioskLeaseSigner
{
    string PublicKeyPem { get; }
    SignedSessionLease Sign(SessionLeasePayload payload);
}

public sealed class KioskLeaseSigner : IKioskLeaseSigner, IDisposable
{
    private readonly RSA _rsa;

    public KioskLeaseSigner(IConfiguration config, IServiceScopeFactory scopes)
    {
        _rsa = RSA.Create(2048);
        var pem = config["GAMECLUB_KIOSK_LEASE_SIGNING_KEY_PEM"];
        if (!string.IsNullOrWhiteSpace(pem))
        {
            _rsa.ImportFromPem(pem);
            return;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = db.KioskLeaseSigningKeys.AsNoTracking().OrderBy(k => k.Id).FirstOrDefault();
        if (row is not null)
        {
            _rsa.ImportFromPem(row.PrivateKeyPem);
            return;
        }

        var privatePem = _rsa.ExportPkcs8PrivateKeyPem();
        db.KioskLeaseSigningKeys.Add(new KioskLeaseSigningKeyEntity
        {
            PrivateKeyPem = privatePem,
            CreatedAtUtc = DateTime.UtcNow,
        });
        db.SaveChanges();
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
