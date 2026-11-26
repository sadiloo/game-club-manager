using Microsoft.AspNetCore.Identity;

namespace GameClub.Kiosk.Core.Admin;

public sealed class KioskCredentialHashRow
{
    public string Role { get; set; } = "";
}

public static class KioskCredentialVerifier
{
    private static readonly PasswordHasher<KioskCredentialHashRow> Hasher = new();

    public static bool Verify(string storedHash, string password, string role)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        var row = new KioskCredentialHashRow { Role = role };
        var result = Hasher.VerifyHashedPassword(row, storedHash, password);
        return result is not PasswordVerificationResult.Failed;
    }
}