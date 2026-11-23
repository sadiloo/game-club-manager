using System.Security.Cryptography;
using System.Text;
using GameClub.Kiosk.Core.Lease;
using GameClub.Kiosk.Core.Protocol;
#if !NET10_0_OR_GREATER
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
#endif

namespace GameClub.Kiosk.Core.Guard;

public sealed class InternetCommandVerifier
{
    private readonly RSA _rsa = RSA.Create();

    public InternetCommandVerifier(string? stationPublicKeyPem)
    {
        if (!string.IsNullOrWhiteSpace(stationPublicKeyPem))
        {
            ImportPublicKeyPem(stationPublicKeyPem!);
        }
    }

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

    public bool TryVerify(SignedInternetCommand command, out string error)
    {
        error = "";
        if (_rsa.KeySize == 0)
        {
            error = "rejected_signature";
            return false;
        }

        try
        {
            var canonical = LeaseVerifier.CanonicalJson(command.Payload);
            var data = Encoding.UTF8.GetBytes(canonical);
            var sig = Convert.FromBase64String(command.SignatureBase64);
            if (!_rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                error = "rejected_signature";
                return false;
            }

            return true;
        }
        catch
        {
            error = "rejected_signature";
            return false;
        }
    }
}