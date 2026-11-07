using GameClub.Billing;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;

namespace GameClub.Server.Operator;

internal static class OperatorUsageMath
{
    internal static (Dictionary<string, long> ByStation, Dictionary<int, long> ByHour) Accumulate(
        IEnumerable<PlaySessionEntity> sessions,
        DateTime fromUtc,
        DateTime toUtc)
    {
        var byStation = new Dictionary<string, long>();
        var byHour = new Dictionary<int, long>();
        foreach (var session in sessions)
        {
            foreach (var seg in session.Segments)
            {
                var segEnd = seg.EndUtc ?? toUtc;
                foreach (var (startUtc, endUtc) in ActiveSubIntervals(seg.StartUtc, segEnd, session.Pauses, fromUtc, toUtc))
                {
                    AccumulateInterval(startUtc, endUtc, seg.StationId, byStation, byHour);
                }
            }
        }

        return (byStation, byHour);
    }

    private static IEnumerable<(DateTime StartUtc, DateTime EndUtc)> ActiveSubIntervals(
        DateTime segStartUtc,
        DateTime segEndUtc,
        IReadOnlyList<PauseIntervalEntity> pauses,
        DateTime queryFromUtc,
        DateTime queryToUtc)
    {
        var start = segStartUtc < queryFromUtc ? queryFromUtc : segStartUtc;
        var end = segEndUtc > queryToUtc ? queryToUtc : segEndUtc;
        if (end <= start)
        {
            yield break;
        }

        var pausesInRange = pauses
            .Select(p => (Start: p.StartUtc, End: p.EndUtc ?? end))
            .Where(p => p.End > start && p.Start < end)
            .OrderBy(p => p.Start)
            .ToList();

        var cursor = start;
        foreach (var pause in pausesInRange)
        {
            var pauseStart = pause.Start < cursor ? cursor : pause.Start;
            var pauseEnd = pause.End > end ? end : pause.End;
            if (pauseStart > cursor)
            {
                yield return (cursor, pauseStart);
            }

            cursor = pauseEnd > cursor ? pauseEnd : cursor;
        }

        if (cursor < end)
        {
            yield return (cursor, end);
        }
    }

    private static void AccumulateInterval(
        DateTime startUtc,
        DateTime endUtc,
        string stationId,
        Dictionary<string, long> byStation,
        Dictionary<int, long> byHour)
    {
        var cursor = startUtc;
        while (cursor < endUtc)
        {
            var tehran = TimeMapping.ToTehran(cursor);
            var nextHourUtc = NextTehranHourBoundaryUtc(cursor);
            var nextMidnightUtc = NextTehranMidnightUtc(cursor);
            var chunkEnd = endUtc;
            if (nextHourUtc < chunkEnd)
            {
                chunkEnd = nextHourUtc;
            }

            if (nextMidnightUtc < chunkEnd)
            {
                chunkEnd = nextMidnightUtc;
            }

            var seconds = (long)Math.Max(0, (chunkEnd - cursor).TotalSeconds);
            if (seconds > 0)
            {
                byStation[stationId] = byStation.GetValueOrDefault(stationId) + seconds;
                byHour[tehran.Hour] = byHour.GetValueOrDefault(tehran.Hour) + seconds;
            }

            cursor = chunkEnd;
        }
    }

    private static DateTime NextTehranHourBoundaryUtc(DateTime utc)
    {
        var tehran = TimeMapping.ToTehran(utc);
        var next = new DateTimeOffset(tehran.Year, tehran.Month, tehran.Day, tehran.Hour, 0, 0, TehranTime.Offset)
            .AddHours(1);
        return next.UtcDateTime;
    }

    private static DateTime NextTehranMidnightUtc(DateTime utc)
    {
        var tehran = TimeMapping.ToTehran(utc);
        var next = new DateTimeOffset(tehran.Year, tehran.Month, tehran.Day, 0, 0, 0, TehranTime.Offset)
            .AddDays(1);
        return next.UtcDateTime;
    }
}
