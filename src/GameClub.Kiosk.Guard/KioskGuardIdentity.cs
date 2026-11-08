using System.Security.Principal;
using GameClub.Kiosk.Core.Security;

namespace GameClub.Kiosk.Guard;

internal sealed class KioskGuardIdentity
{
    public string UserName { get; }
    public string Sid { get; }
    public bool IsResolved { get; }

    private KioskGuardIdentity(string userName, string sid, bool resolved)
    {
        UserName = userName;
        Sid = sid;
        IsResolved = resolved;
    }

    internal static KioskGuardIdentity Resolve()
    {
        var user = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_USER") ?? "CheckpointKiosk";
        var configuredSid = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_SID");
        if (KioskGuardIdentityCore.TryResolveSid(configuredSid, user, out var sid, out _))
        {
            return new KioskGuardIdentity(user, sid, true);
        }

        try
        {
            var account = new NTAccount(user);
            var translated = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
            return new KioskGuardIdentity(user, translated.Value, true);
        }
        catch
        {
            return new KioskGuardIdentity(user, "", false);
        }
    }
}
