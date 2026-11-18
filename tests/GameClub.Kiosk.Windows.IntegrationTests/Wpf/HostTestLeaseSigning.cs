using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GameClub.Kiosk.Core.Lease;
using GameClub.Kiosk.Core.Protocol;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;

namespace GameClub.Kiosk.Windows.IntegrationTests.Wpf;

internal static class HostTestLeaseSigning
{
    private static readonly RSA Rsa = RSA.Create(2048);

    public static string PublicKeyPem
    {
        get
        {
#if NET48
            var pub = DotNetUtilities.GetRsaPublicKey(Rsa.ExportParameters(false));
            using var writer = new StringWriter();
            new PemWriter(writer).WriteObject(pub);
            return writer.ToString();
#else
            return Rsa.ExportRSAPublicKeyPem();
#endif
        }
    }

    public static SignedSessionLease SignLease(Guid sessionId, string stationId, DateTime authorizedUntilUtc, int leaseVersion = 1)
    {
        var payload = new SessionLeasePayload
        {
            LeaseId = Guid.NewGuid(),
            SessionId = sessionId,
            StationId = stationId,
            AuthorizedUntilUtc = authorizedUntilUtc,
            IssuedAtUtc = DateTime.UtcNow,
            LeaseVersion = leaseVersion,
        };
        var canonical = LeaseVerifier.CanonicalJson(payload);
        var data = Encoding.UTF8.GetBytes(canonical);
        return new SignedSessionLease
        {
            Payload = payload,
            SignatureBase64 = Convert.ToBase64String(
                Rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
        };
    }
}