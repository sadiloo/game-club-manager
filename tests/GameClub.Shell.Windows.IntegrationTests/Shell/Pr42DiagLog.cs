#if NET48
using System;
using System.Diagnostics;

namespace GameClub.Shell.Windows.IntegrationTests.Shell;

/// <summary>PR42 R6 diagnosis-only logging (no behavior change).</summary>
internal static class Pr42DiagLog
{
    public static void Write(string message)
    {
        var line = $"[pr42-r6-diag] {DateTime.UtcNow:O} {message}";
        Console.WriteLine(line);
        Trace.WriteLine(line);
    }
}
#endif