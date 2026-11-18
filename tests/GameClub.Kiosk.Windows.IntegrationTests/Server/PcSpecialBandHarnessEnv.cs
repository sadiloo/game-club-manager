#if NET10_0_OR_GREATER
using GameClub.Billing;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

internal static class PcSpecialBandHarnessEnv
{
    public const string EnabledKey = "TestHarness__PcSpecialBand__Enabled";
    public const string NormalStartKey = "TestHarness__PcSpecialBand__PcNormalStart";
    public const string NormalEndKey = "TestHarness__PcSpecialBand__PcNormalEnd";
    public const string VipStartKey = "TestHarness__PcSpecialBand__PcVipStart";
    public const string VipEndKey = "TestHarness__PcSpecialBand__PcVipEnd";

    public static void ApplyDayTestBandAwayFromNow(IDictionary<string, string?> target)
    {
        var (start, end) = ComputeBandAwayFromNow(minimumMargin: TimeSpan.FromHours(3));
        WriteBand(target, start, end);
    }

    public static void ApplyNightBandCoveringNow(IDictionary<string, string?> target)
    {
        var now = TehranTime.FromUtc(DateTime.UtcNow);
        var startLocal = now.AddHours(-3);
        var endLocal = now.AddHours(3);
        var start = new TimeOfDay(startLocal.Hour, startLocal.Minute);
        var end = new TimeOfDay(endLocal.Hour, endLocal.Minute);
        WriteBand(target, start, end);
    }

    public static void Clear(IDictionary<string, string?> target)
    {
        target.Remove(EnabledKey);
        target.Remove(NormalStartKey);
        target.Remove(NormalEndKey);
        target.Remove(VipStartKey);
        target.Remove(VipEndKey);
    }

    private static void WriteBand(IDictionary<string, string?> target, TimeOfDay start, TimeOfDay end)
    {
        var startText = $"{start.Hour:00}:{start.Minute:00}";
        var endText = $"{end.Hour:00}:{end.Minute:00}";
        target[EnabledKey] = "true";
        target[NormalStartKey] = startText;
        target[NormalEndKey] = endText;
        target[VipStartKey] = startText;
        target[VipEndKey] = endText;
    }

    internal static (TimeOfDay Start, TimeOfDay End) ComputeBandAwayFromNow(TimeSpan minimumMargin)
    {
        var now = TehranTime.FromUtc(DateTime.UtcNow);
        var minuteOfDay = (now.Hour * 60) + now.Minute;
        var center = (minuteOfDay + (12 * 60)) % (24 * 60);
        var startMin = (center - 15 + (24 * 60)) % (24 * 60);
        var endMin = (center + 15) % (24 * 60);
        return (MinuteOfDayToTime(startMin), MinuteOfDayToTime(endMin));

        static TimeOfDay MinuteOfDayToTime(int minuteOfDay)
        {
            var h = minuteOfDay / 60;
            var m = minuteOfDay % 60;
            return new TimeOfDay(h, m);
        }
    }
}
#endif