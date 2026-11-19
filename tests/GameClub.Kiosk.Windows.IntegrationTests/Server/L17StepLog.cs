#if NET48
#nullable disable
using System;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

internal static class L17StepLog
{
    internal static void Write(string step, string detail = "")
    {
        var ms = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        var line = string.IsNullOrEmpty(detail)
            ? $"L17_STEP utc_ms={ms} step={step}"
            : $"L17_STEP utc_ms={ms} step={step} {detail}";
        Console.WriteLine(line);
        Console.Out.Flush();
    }
}

internal static class StabStepLog
{
    internal static void Write(string step, string detail = "")
    {
        var ms = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        var line = string.IsNullOrEmpty(detail)
            ? $"STAB_STEP utc_ms={ms} step={step}"
            : $"STAB_STEP utc_ms={ms} step={step} {detail}";
        Console.WriteLine(line);
        Console.Out.Flush();
    }
}
#endif