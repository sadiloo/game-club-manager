using GameClub.Billing;

namespace GameClub.Kiosk.Core;

/// <summary>
/// Offline log timestamps are Tehran wall time at a fixed +03:30, rebuilt from the UTC instant.
/// The PC's time zone, including the old Iran daylight-saving offset on Windows 7, is not stored.
/// </summary>
public static class TehranStamp
{
    public static DateTimeOffset Normalize(DateTimeOffset instant) => TehranTime.Ensure(instant);

    public static string Format(DateTimeOffset instant) => TehranTime.Ensure(instant).ToString("o");
}

/// <summary>One line of the guard log. The guard appends this when it closes a process.</summary>
public static class GuardLog
{
    public static string Kill(DateTimeOffset at, string processName, string reason) =>
        TehranStamp.Format(at) + " killed " + processName + " " + reason;
}
