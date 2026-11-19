using GameClub.Billing;

namespace GameClub.Server.Testing;

/// <summary>Tehran special-band windows for integration and test-host runs (not production config).</summary>
public static class PcSpecialBandHarnessSchedule
{
    public static (TimeOfDay Start, TimeOfDay End) DayBandAwayFromNow(TimeSpan minimumMargin)
    {
        _ = minimumMargin;
        var now = TehranTime.FromUtc(DateTime.UtcNow);
        var minuteOfDay = (now.Hour * 60) + now.Minute;
        var center = (minuteOfDay + (12 * 60)) % (24 * 60);
        var startMin = (center - 15 + (24 * 60)) % (24 * 60);
        var endMin = (center + 15) % (24 * 60);
        return (MinuteOfDayToTime(startMin), MinuteOfDayToTime(endMin));
    }

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
}