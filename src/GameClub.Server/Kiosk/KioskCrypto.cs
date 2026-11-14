using System.Security.Cryptography;
using System.Text;

namespace GameClub.Server.Kiosk;

internal static class KioskCrypto
{
    public static string HashDeviceKey(string deviceKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(deviceKey));
        return Convert.ToHexString(hash);
    }

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split(':', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[0]);
        var expected = Convert.FromBase64String(parts[1]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static string NormalizeLoginName(string loginName)
        => loginName.Trim().ToUpperInvariant();

    public static string NewDeviceKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
