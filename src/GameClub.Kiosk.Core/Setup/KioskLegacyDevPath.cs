namespace GameClub.Kiosk.Core.Setup;

/// <summary>CI/dev bypass: both env vars set keeps legacy F8/F9 and hides first-run setup (B-6i).</summary>
public static class KioskLegacyDevPath
{
    public static bool IsActive()
    {
        var url = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL");
        var admin = Environment.GetEnvironmentVariable("CHECKPOINT_ADMIN_PASSWORD");
        return !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(admin);
    }
}