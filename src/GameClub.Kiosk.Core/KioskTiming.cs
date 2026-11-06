namespace GameClub.Kiosk.Core;

/// <summary>
/// Durations the kiosk and the guard agree on. Play is counted in whole seconds.
/// A gap longer than <see cref="PowerCutGap"/> between the last checkpoint and "now"
/// is treated as the PC having been off: that downtime is not play.
/// </summary>
public static class KioskTiming
{
    public static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The station resumes from the last checkpoint when it returns inside this gap.
    /// The outage itself is not play. Longer than this, the session closes at the checkpoint.
    /// </summary>
    public static readonly TimeSpan PowerCutGap = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan FlushBackoffCap = TimeSpan.FromMinutes(2);

    public static TimeSpan FlushBackoff(int attempts)
    {
        var shift = attempts < 1 ? 0 : Math.Min(attempts, 6);
        var seconds = 1 << shift;
        var delay = TimeSpan.FromSeconds(seconds);
        return delay > FlushBackoffCap ? FlushBackoffCap : delay;
    }
}
