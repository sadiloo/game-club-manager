namespace GameClub.Kiosk.Core;

public sealed record PipeTrustee(string Sid, string Rights);

/// <summary>
/// Who may open the kiosk pipe. The guard runs as SYSTEM. The kiosk user may read and write.
/// Everyone else, including other sessions, is absent so a default DACL cannot block or expose the pipe.
/// </summary>
public static class PipeAccess
{
    public const string SystemSid = "S-1-5-18";

    public static IReadOnlyList<PipeTrustee> ForKioskUser(string? kioskUserSid)
    {
        if (string.IsNullOrWhiteSpace(kioskUserSid))
        {
            throw new InvalidOperationException("The kiosk user SID is required before the pipe is created.");
        }

        var sid = kioskUserSid!.Trim();
        if (string.Equals(sid, SystemSid, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The kiosk user cannot be SYSTEM.");
        }

        return
        [
            new PipeTrustee(SystemSid, "full"),
            new PipeTrustee(sid, "readwrite"),
        ];
    }
}
