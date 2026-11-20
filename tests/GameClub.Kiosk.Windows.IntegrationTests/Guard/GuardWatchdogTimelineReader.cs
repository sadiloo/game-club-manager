#if NET48
#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    internal static string ReadAllTextWithShare(int maxAttempts = 5)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    WatchdogLogPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt + 1 < maxAttempts)
            {
                Thread.Sleep(50 << attempt);
            }
        }

        using var lastTry = new FileStream(
            WatchdogLogPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var lastReader = new StreamReader(lastTry);
        return lastReader.ReadToEnd();
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

    internal static void AssertExactlyOneGuardRestart(long logStartOffset)
    {
        var launched = ReadLinesSince(logStartOffset)
            .Count(l => l.Contains("guard.restart", StringComparison.Ordinal)
                && l.Contains("launched_pid_", StringComparison.Ordinal));
        Assert.Equal(1, launched);
    }

    internal static async Task WaitForTimelineMarkerAsync(
        long logStartOffset,
        string marker,
        TimeSpan budget,
        int pollMs = 100)
    {
        var deadline = DateTime.UtcNow.Add(budget);
        while (DateTime.UtcNow < deadline)
        {
            if (ReadLinesSince(logStartOffset).Any(l => l.Contains(marker, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(pollMs);
        }

        Assert.Fail(
            $"Timed out after {(int)budget.TotalSeconds}s waiting for watchdog marker containing: {marker}");
    }

    internal static void AssertPipeRestartHandlingOrder(long logStartOffset, Action<string> trace)
    {
        var lines = ReadLinesSince(logStartOffset).ToArray();
        var timeline = lines
            .Where(l => l.Contains("GUARD_TIMELINE", StringComparison.Ordinal)
                || l.Contains("PIPE_TIMELINE", StringComparison.Ordinal))
            .ToList();
        foreach (var line in timeline)
        {
            trace(line);
        }

        int IndexOf(string fragment) =>
            Array.FindIndex(lines, l => l.Contains(fragment, StringComparison.Ordinal));

        var listeningIdx = IndexOf("GUARD_TIMELINE event=pipe_listening");
        var lockExitIdx = IndexOf("early_reassert_off_lock_exit");
        var statusHandledIdx = IndexOf("GUARD_TIMELINE event=pipe_request_handled ticket=-1");
        var holdReleaseIdx = IndexOf("pipe_request_hold_release ticket=0");
        var mutatingHandledIdx = IndexOf("GUARD_TIMELINE event=pipe_request_handled ticket=0");
        var pipeReadyIdx = IndexOf("GUARD_TIMELINE event=pipe_ready");

        Assert.True(listeningIdx >= 0, "watchdog must log GUARD_TIMELINE event=pipe_listening");
        Assert.True(lockExitIdx >= 0, "watchdog must log PIPE_TIMELINE early_reassert_off_lock_exit");
        Assert.True(statusHandledIdx >= 0, "watchdog must log GUARD_TIMELINE event=pipe_request_handled ticket=-1 for status request");
        Assert.True(holdReleaseIdx >= 0, "watchdog must log pipe_request_hold_release ticket=0 for held OFF apply");
        Assert.True(mutatingHandledIdx >= 0, "watchdog must log GUARD_TIMELINE event=pipe_request_handled ticket=0 for held OFF apply");
        Assert.True(pipeReadyIdx >= 0, "watchdog must log GUARD_TIMELINE event=pipe_ready for guard2 run");
        Assert.True(listeningIdx < lockExitIdx, "pipe_listening must precede early_reassert_off_lock_exit");
        Assert.True(statusHandledIdx < lockExitIdx, "status pipe_request_handled ticket=-1 must precede early_reassert_off_lock_exit");
        Assert.True(lockExitIdx < holdReleaseIdx, "early_reassert_off_lock_exit must precede pipe_request_hold_release ticket=0");
        Assert.True(holdReleaseIdx < mutatingHandledIdx, "pipe_request_hold_release ticket=0 must precede pipe_request_handled ticket=0");
        Assert.True(mutatingHandledIdx < pipeReadyIdx, "pipe_request_handled ticket=0 must precede pipe_ready");
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