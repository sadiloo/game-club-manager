using System.Security.Cryptography;
using System.Text;

namespace GameClub.Server.Testing;

/// <summary>CI harness fingerprints for kiosk lease signing keys (never logs PEM material).</summary>
public static class KioskLeaseSigningKeyDiagnostics
{
    public static string Sha256Prefix16(string material)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public static string FormatFingerprintLine(string publicKeyPem)
        => $"LEASE_KEY_FINGERPRINT sha256={Sha256Prefix16(publicKeyPem)}";
}