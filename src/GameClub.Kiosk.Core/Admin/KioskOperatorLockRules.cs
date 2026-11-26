namespace GameClub.Kiosk.Core.Admin;

/// <summary>Operator «قفل» / «باز کردن قفل» connectivity rules (K-OP addendum).</summary>
public static class KioskOperatorLockRules
{
    public const string OfflineSessionLockErrorKey = "kiosk.oplock.err.offline";

    public const string OfflineFreeUseStartErrorKey = "kiosk.freeuse.err.offline";

    public static bool CanLockOpenSessionOnline(bool transportConnected) => transportConnected;

    public static bool CanStartFreeUseOnline(bool transportConnected) => transportConnected;

    public static bool CanEndFreeUseWhileOffline => true;
}