#if NET48
#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

internal static class GuardWatchdogTimelineReader
{
    private static string WatchdogLogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Checkpoint",
            "guard",
            "watchdog.log");

    internal static long CurrentLogLength()
    {
        try
        {
            return new FileInfo(WatchdogLogPath).Length;
        }
        catch
        {
            return 0;
        }
    }

    internal static IReadOnlyList<string> ReadLinesSince(long byteOffset)
    {
        try
        {
            if (!File.Exists(WatchdogLogPath))
            {
                return Array.Empty<string>();
            }

            using var stream = new FileStream(WatchdogLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (byteOffset > stream.Length)
            {
                byteOffset = 0;
            }

            stream.Seek(byteOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd()
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    internal static void AssertEarlyOffReassertBeforePipeReady(long logStartOffset, Action<string> trace)
    {
        var lines = ReadLinesSince(logStartOffset);
        var timeline = lines
            .Where(l => l.Contains("GUARD_TIMELINE", StringComparison.Ordinal) || l.Contains("PIPE_TIMELINE", StringComparison.Ordinal))
            .ToList();
        foreach (var line in timeline)
        {
            trace(line);
        }

        var earlyIdx = Array.FindIndex(lines.ToArray(), l =>
            l.Contains("GUARD_TIMELINE event=early_reassert_off", StringComparison.Ordinal));
        var pipeIdx = Array.FindIndex(lines.ToArray(), l =>
            l.Contains("GUARD_TIMELINE event=pipe_ready", StringComparison.Ordinal));

        Assert.True(earlyIdx >= 0, "watchdog must log GUARD_TIMELINE event=early_reassert_off for guard2 run");
        Assert.True(pipeIdx >= 0, "watchdog must log GUARD_TIMELINE event=pipe_ready for guard2 run");
        Assert.True(
            earlyIdx < pipeIdx,
            $"early_reassert_off (line {earlyIdx}) must precede pipe_ready (line {pipeIdx}); full_timeline={string.Join(" | ", timeline)}");
    }
}
#endif