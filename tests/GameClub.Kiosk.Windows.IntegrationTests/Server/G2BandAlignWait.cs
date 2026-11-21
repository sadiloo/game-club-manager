#if NET10_0_OR_GREATER
using GameClub.Billing;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

/// <summary>
/// Pure helper for G2 integration: avoid starting the ~11-minute session when a PC special-hours
/// band edge falls inside the next 13 minutes of wall clock (Tehran).
/// </summary>
public static class G2BandAlignWait
{
    public static readonly TimeSpan ApproachWindow = TimeSpan.FromMinutes(13);

    public static readonly TimeSpan PostEdgeBuffer = TimeSpan.FromSeconds(5);

    public static IReadOnlyList<TimeOfDay> BandEdges(TimeBand band) => new[] { band.Start, band.End };

    public readonly record struct Plan(DateTimeOffset? WaitUntilUtc, DateTimeOffset? TriggeringEdgeTehran, long WaitMilliseconds);

    /// <summary>
    /// The next calendar edge at <paramref name="edge"/> on or after <paramref name="now"/> (Tehran).
    /// An instant exactly on the edge uses that day's wall time (not rolled forward).
    /// </summary>
    public static DateTimeOffset UpcomingEdge(DateTimeOffset now, TimeOfDay edge)
    {
        now = TehranTime.Ensure(now);
        var date = TehranTime.Date(now);
        var onDay = TehranTime.At(date, edge);
        return now <= onDay ? onDay : TehranTime.At(date.AddDays(1), edge);
    }

    /// <summary>
    /// Wait until <c>edge + 5s</c> when the upcoming edge is at most <see cref="ApproachWindow"/> away.
    /// The window is inclusive at exactly 13 minutes: <c>now == edge − 13min</c> waits;
    /// one tick earlier does not.
    /// </summary>
    public static Plan ComputePlan(DateTimeOffset nowUtc, IEnumerable<TimeOfDay> edges)
    {
        var now = nowUtc.ToUniversalTime();
        var nowTehran = TehranTime.Ensure(now);
        DateTimeOffset? waitUntilTehran = null;
        DateTimeOffset? triggeringEdge = null;

        foreach (var edge in edges)
        {
            // UpcomingEdge never returns a wall time before nowTehran, so untilEdge is never negative.
            var upcoming = UpcomingEdge(nowTehran, edge);
            var untilEdge = upcoming - nowTehran;
            if (untilEdge > ApproachWindow)
            {
                continue;
            }

            var candidateWait = upcoming.Add(PostEdgeBuffer);
            if (waitUntilTehran is null || candidateWait > waitUntilTehran)
            {
                waitUntilTehran = candidateWait;
                triggeringEdge = upcoming;
            }
        }

        if (waitUntilTehran is null)
        {
            return new Plan(null, null, 0);
        }

        var waitUntilUtc = waitUntilTehran.Value.ToUniversalTime();
        var waitMs = (long)Math.Ceiling((waitUntilUtc - now).TotalMilliseconds);
        if (waitMs < 0)
        {
            waitMs = 0;
        }

        return new Plan(waitUntilUtc, triggeringEdge, waitMs);
    }

    /// <summary>
    /// Fails when session start, powered-span end, or any checkpoint lies on different sides of a band edge.
    /// </summary>
    public static void AssertSameSideOfAllBandEdges(
        TimeBand band,
        DateTimeOffset sessionStartUtc,
        IEnumerable<DateTime> checkpointAtUtc,
        DateTimeOffset poweredSpanEndUtc)
    {
        var edges = BandEdges(band);
        var instants = new List<DateTimeOffset>
        {
            TehranTime.FromUtc(sessionStartUtc.ToUniversalTime().DateTime),
            TehranTime.FromUtc(poweredSpanEndUtc.ToUniversalTime().DateTime),
        };
        instants.AddRange(checkpointAtUtc.Select(t => TehranTime.FromUtc(DateTime.SpecifyKind(t, DateTimeKind.Utc))));
        instants = instants.OrderBy(t => t).ToList();
        if (instants.Count == 0)
        {
            throw new InvalidOperationException("G2 band assert: no instants.");
        }

        var min = instants[0];
        var max = instants[^1];

        for (var day = TehranTime.Date(min); day.CompareTo(TehranTime.Date(max)) <= 0; day = day.AddDays(1))
        {
            foreach (var edge in edges)
            {
                var edgeInstant = TehranTime.At(day, edge);
                if (edgeInstant < min || edgeInstant > max)
                {
                    continue;
                }

                var before = instants.Any(t => t < edgeInstant);
                var atOrAfter = instants.Any(t => t >= edgeInstant);
                if (before && atOrAfter)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"G2 band-edge straddle: pilot edge {edgeInstant:O} falls between session/checkpoint instants "
                        + $"({min:O} .. {max:O}).");
                }
            }
        }
    }
}
#endif