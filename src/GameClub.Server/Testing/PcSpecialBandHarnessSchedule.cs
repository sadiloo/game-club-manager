using GameClub.Billing;

namespace GameClub.Server.Testing;

/// <summary>Tehran special-band windows for integration and test-host runs (not production config).</summary>
public static class PcSpecialBandHarnessSchedule
{
    public static (TimeOfDay Start, TimeOfDay End) DayBandAwayFromNow(TimeSpan minimumMargin)
    {
        var startUtc = DateTime.UtcNow;
        var endUtc = startUtc + minimumMargin;
        return BandAwayFromUtcWindow(startUtc, endUtc, minimumMargin);
    }

    /// <summary>Place a 30-minute special band at least <paramref name="margin"/> away from the whole UTC window (G2 oracle).</summary>
    public static (TimeOfDay Start, TimeOfDay End) BandAwayFromUtcWindow(DateTime windowStartUtc, DateTime windowEndUtc, TimeSpan margin)
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

        throw new InvalidOperationException(
            $"Could not place harness special band {margin.TotalHours:F0}h away from window {windowStartUtc:o}–{windowEndUtc:o}.");
    }

    public static PcSpecialBandHarnessOptions OptionsForG2Oracle(TimeSpan expectedRunDuration)
        => ToOptions(BandAwayFromUtcWindow(DateTime.UtcNow, DateTime.UtcNow + expectedRunDuration, TimeSpan.FromHours(3)));

    public static (TimeOfDay Start, TimeOfDay End) NightBandCoveringNow()
    {
        var now = TehranTime.FromUtc(DateTime.UtcNow);
        var startLocal = now.AddHours(-3);
        var endLocal = now.AddHours(3);
        return (
            new TimeOfDay(startLocal.Hour, startLocal.Minute),
            new TimeOfDay(endLocal.Hour, endLocal.Minute));
    }

    public static PcSpecialBandHarnessOptions OptionsForBand(string mode)
        => mode.Equals("night", StringComparison.OrdinalIgnoreCase)
            ? ToOptions(NightBandCoveringNow())
            : ToOptions(DayBandAwayFromNow(TimeSpan.FromHours(3)));

    public static PcSpecialBandHarnessOptions ToOptions((TimeOfDay Start, TimeOfDay End) band)
    {
        var startText = $"{band.Start.Hour:00}:{band.Start.Minute:00}";
        var endText = $"{band.End.Hour:00}:{band.End.Minute:00}";
        return new PcSpecialBandHarnessOptions
        {
            Enabled = true,
            PcNormalStart = startText,
            PcNormalEnd = endText,
            PcVipStart = startText,
            PcVipEnd = endText,
        };
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
}