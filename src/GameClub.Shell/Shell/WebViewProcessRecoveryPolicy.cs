using System;
using System.Collections.Generic;
using System.Linq;

namespace GameClub.Shell.Shell;

internal static class WebViewProcessRecoveryBackoff
{
    public static readonly TimeSpan Delay1 = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Delay2 = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Delay3 = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RollingWindow = TimeSpan.FromMinutes(5);
    public const int MaxAttemptsPerRollingWindow = 3;
}

internal sealed class WebViewProcessRecoveryPolicy
{
    private readonly List<DateTime> _attemptUtcTimestamps = new();

    public int RecordAttemptAndGetIndex(DateTime utcNow)
    {
        Prune(utcNow);
        _attemptUtcTimestamps.Add(utcNow);
        return _attemptUtcTimestamps.Count;
    }

    public bool CanRetry(DateTime utcNow)
    {
        Prune(utcNow);
        return _attemptUtcTimestamps.Count < WebViewProcessRecoveryBackoff.MaxAttemptsPerRollingWindow;
    }

    public TimeSpan? GetBackoffForAttempt(int attemptIndex)
    {
        return attemptIndex switch
        {
            1 => WebViewProcessRecoveryBackoff.Delay1,
            2 => WebViewProcessRecoveryBackoff.Delay2,
            3 => WebViewProcessRecoveryBackoff.Delay3,
            _ => null,
        };
    }

    public void Reset()
    {
        _attemptUtcTimestamps.Clear();
    }

    private void Prune(DateTime utcNow)
    {
        var cutoff = utcNow - WebViewProcessRecoveryBackoff.RollingWindow;
        _attemptUtcTimestamps.RemoveAll(t => t < cutoff);
    }

    public int AttemptCountInWindow(DateTime utcNow)
    {
        Prune(utcNow);
        return _attemptUtcTimestamps.Count;
    }
}