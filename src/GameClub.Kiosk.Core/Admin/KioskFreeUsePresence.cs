namespace GameClub.Kiosk.Core.Admin;

/// <summary>Display-only no-contact for open free-use rows (3 minutes without kiosk contact).</summary>
public static class KioskFreeUsePresence
{
    public const int NoContactAfterSeconds = 180;

    public static bool IsNoContact(DateTime? lastContactUtc, DateTime nowUtc, bool isOpen)
        => isOpen
            && lastContactUtc is not null
            && (nowUtc - lastContactUtc.Value).TotalSeconds > NoContactAfterSeconds;

    public static DateTime EffectiveDurationEnd(
        DateTime? endedAtUtc,
        DateTime? lastContactUtc,
        DateTime nowUtc,
        bool noContact)
    {
        if (endedAtUtc is not null)
        {
            return endedAtUtc.Value;
        }

        if (noContact && lastContactUtc is not null)
        {
            return lastContactUtc.Value;
        }

        return nowUtc;
    }
}