namespace GameClub.Kiosk.Core.Security;

/// <summary>Fail-closed identity bootstrap shared with the Windows guard service.</summary>
public static class KioskGuardIdentityCore
{
    public static bool TryResolveSid(string? configuredSid, string? kioskUser, out string sid, out string error)
    {
        sid = "";
        error = "";
        if (!string.IsNullOrWhiteSpace(configuredSid))
        {
            sid = configuredSid!.Trim();
            return true;
        }

        if (string.IsNullOrWhiteSpace(kioskUser))
        {
            error = "kiosk_user_missing";
            return false;
        }

        error = "kiosk_sid_missing";
        return false;
    }
}
