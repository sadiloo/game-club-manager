namespace GameClub.Kiosk.Core.Runtime;

public static class KioskReconnectBackoff
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(2);

    public static TimeSpan DelayForAttempt(int attempt)
    {
        var cappedExponent = attempt;
        if (cappedExponent > 4)
        {
            cappedExponent = 4;
        }

        var multiplier = 1 << cappedExponent;
        var delay = TimeSpan.FromTicks(Base.Ticks * multiplier);
        return delay > Cap ? Cap : delay;
    }
}