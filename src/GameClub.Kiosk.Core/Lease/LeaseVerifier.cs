using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameClub.Kiosk.Core.Protocol;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;

namespace GameClub.Kiosk.Core.Lease;

/// <summary>Verify-only lease check using the server public key delivered at pairing.</summary>
public sealed class LeaseVerifier
{
    private readonly RSA _rsa = RSA.Create();

    public LeaseVerifier(string? publicKeyPem)
    {
        if (!string.IsNullOrWhiteSpace(publicKeyPem))
        {
            ImportPublicKeyPem(publicKeyPem!);
        }
    }

    public bool TryVerify(SignedSessionLease lease)
    {
        if (_rsa.KeySize == 0)
        {
            return false;
        }

        var canonical = JsonSerializer.Serialize(lease.Payload);
        var data = Encoding.UTF8.GetBytes(canonical);
        var sig = Convert.FromBase64String(lease.SignatureBase64);
        return _rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public static string CanonicalJson(SessionLeasePayload p)
        => JsonSerializer.Serialize(p);

    private void ImportPublicKeyPem(string pem)
    {
#if NET10_0_OR_GREATER
        _rsa.ImportFromPem(pem);
#else
        using var reader = new StringReader(pem);
        var keyObject = new PemReader(reader).ReadObject();
        if (keyObject is RsaKeyParameters rsaPublic)
        {
            _rsa.ImportParameters(DotNetUtilities.ToRSAParameters(rsaPublic));
            return;
        }

        throw new InvalidOperationException("Expected RSA public key PEM.");
#endif
    }
}
