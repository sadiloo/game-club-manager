#nullable disable
using System;
using System.Globalization;
using GameClub.Billing;

namespace GameClub.Kiosk.Windows.IntegrationTests;

/// <summary>net48 WPF mirror of TestHost PC-band windows; oracle-tested against Server.Testing on net10.</summary>
internal static class PcSpecialBandLiveRateCardHelperNet48Mirror
{
    internal static (string Start, string End) ResolvePcNormalHarnessWindow(string hostArgs, string bandMode)
    {
        if (!string.IsNullOrWhiteSpace(hostArgs))
        {
            var explicitBand = TryParseExplicitNormalWindow(hostArgs);
            if (explicitBand.HasValue)
            {
                return explicitBand.Value;
            }

            if (TryParseBandToken(hostArgs, out var mode))
            {
                return BandWindowForMode(mode);
            }
        }

        if (string.Equals(bandMode, "night", StringComparison.OrdinalIgnoreCase))
        {
            return BandWindowForMode("night");
        }

        throw new InvalidOperationException(
            "Live WPF special-band helper requires GAMECLUB_SERVER_HOST_ARGS or CHECKPOINT_SERVER_PC_BAND=night.");
    }

    private static (string Start, string End) BandWindowForMode(string mode)
    {
        var band = mode.Equals("night", StringComparison.OrdinalIgnoreCase)
            ? NightBandCoveringNow()
            : DayBandAwayFromNow(TimeSpan.FromHours(3));
        return ($"{band.Start.Hour:00}:{band.Start.Minute:00}", $"{band.End.Hour:00}:{band.End.Minute:00}");
    }

    private static (string Start, string End)? TryParseExplicitNormalWindow(string hostArgs)
    {
        string start = null;
        string end = null;
        var parts = hostArgs.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], "--pc-normal-start", StringComparison.OrdinalIgnoreCase))
            {
                start = parts[++i];
            }
            else if (string.Equals(parts[i], "--pc-normal-end", StringComparison.OrdinalIgnoreCase))
            {
                end = parts[++i];
            }
            else if (string.Equals(parts[i], "--pc-band", StringComparison.OrdinalIgnoreCase))
            {
                i++;
            }
        }

        return start != null && end != null ? (start, end) : null;
    }

    private static bool TryParseBandToken(string hostArgs, out string mode)
    {
        var parts = hostArgs.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], "--pc-band", StringComparison.OrdinalIgnoreCase))
            {
                mode = parts[i + 1];
                return true;
            }
        }

        mode = "";
        return false;
    }

    private static (TimeOfDay Start, TimeOfDay End) NightBandCoveringNow()
    {
        var now = TehranTime.FromUtc(DateTime.UtcNow);
        var startLocal = now.AddHours(-3);
        var endLocal = now.AddHours(3);
        return (
            new TimeOfDay(startLocal.Hour, startLocal.Minute),
            new TimeOfDay(endLocal.Hour, endLocal.Minute));
    }

    private static (TimeOfDay Start, TimeOfDay End) DayBandAwayFromNow(TimeSpan minimumMargin)
    {
        var startUtc = DateTime.UtcNow;
        var endUtc = startUtc + minimumMargin;
        return BandAwayFromUtcWindow(startUtc, endUtc, minimumMargin);
    }

    private static (TimeOfDay Start, TimeOfDay End) BandAwayFromUtcWindow(
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        TimeSpan margin)
    {
        if (windowEndUtc < windowStartUtc)
        {
            (windowStartUtc, windowEndUtc) = (windowEndUtc, windowStartUtc);
        }

        var expandedStart = windowStartUtc - margin;
        var expandedEnd = windowEndUtc + margin;
        var centerTehran = TehranTime.FromUtc(windowStartUtc + TimeSpan.FromTicks((windowEndUtc - windowStartUtc).Ticks / 2));
        var centerMin = (centerTehran.Hour * 60) + centerTehran.Minute;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var offset = attempt * (3 * 60);
            var bandCenter = (centerMin + (12 * 60) + offset) % (24 * 60);
            var bandStart = (bandCenter - 15 + (24 * 60)) % (24 * 60);
            var bandEnd = (bandCenter + 15) % (24 * 60);
            var band = (MinuteOfDayToTime(bandStart), MinuteOfDayToTime(bandEnd));
            if (!HarnessSpecialBandIntersectsUtcWindow(band.Item1, band.Item2, expandedStart, expandedEnd))
            {
                return band;
            }
        }

        throw new InvalidOperationException("Could not place harness special band away from UTC window.");
    }

    private static TimeOfDay MinuteOfDayToTime(int minuteOfDay)
    {
        var h = minuteOfDay / 60;
        var m = minuteOfDay % 60;
        return new TimeOfDay(h, m);
    }

    private static bool HarnessSpecialBandIntersectsUtcWindow(
        TimeOfDay bandStart,
        TimeOfDay bandEnd,
        DateTime expandedStartUtc,
        DateTime expandedEndUtc)
    {
        for (var utc = expandedStartUtc; utc <= expandedEndUtc; utc = utc.AddMinutes(15))
        {
            var local = TehranTime.FromUtc(utc);
            if (IsInsideHarnessSpecialBand(local, bandStart, bandEnd))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInsideHarnessSpecialBand(DateTimeOffset tehranLocal, TimeOfDay bandStart, TimeOfDay bandEnd)
    {
        var minuteOfDay = (tehranLocal.Hour * 60) + tehranLocal.Minute;
        var startMinute = (bandStart.Hour * 60) + bandStart.Minute;
        var endMinute = (bandEnd.Hour * 60) + bandEnd.Minute;
        return startMinute <= endMinute
            ? minuteOfDay >= startMinute && minuteOfDay < endMinute
            : minuteOfDay >= startMinute || minuteOfDay < endMinute;
    }

    internal static TimeOfDay ParseTehranTime(string raw)
    {
        var parts = raw.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minute))
        {
            throw new FormatException($"Invalid harness time '{raw}'; expected HH:mm.");
        }

        return new TimeOfDay(hour, minute);
    }
}