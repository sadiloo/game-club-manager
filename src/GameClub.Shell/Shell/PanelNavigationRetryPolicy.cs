using System;

namespace GameClub.Shell.Shell;

internal static class PanelNavigationRetryBackoff
{
    public static readonly TimeSpan Delay1 = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Delay2 = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Delay3 = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DelayRepeat = TimeSpan.FromSeconds(30);
}

internal sealed class PanelNavigationRetryPolicy
{
    private int _failureCount;
    private DateTime _lastFailureUtc = DateTime.MinValue;

    public int FailureCount => _failureCount;

    public void Reset()
    {
        _failureCount = 0;
        _lastFailureUtc = DateTime.MinValue;
    }

    public void RecordFailure(DateTime utcNow)
    {
        _failureCount++;
        _lastFailureUtc = utcNow;
    }

    public TimeSpan GetDelayAfterFailure(int failureCount)
    {
        return failureCount switch
        {
            1 => PanelNavigationRetryBackoff.Delay1,
            2 => PanelNavigationRetryBackoff.Delay2,
            3 => PanelNavigationRetryBackoff.Delay3,
            _ => PanelNavigationRetryBackoff.DelayRepeat,
        };
    }

    public bool CanAttempt(DateTime utcNow)
    {
        if (_failureCount == 0)
        {
            return true;
        }

        var delay = GetDelayAfterFailure(_failureCount);
        return utcNow >= _lastFailureUtc + delay;
    }

    public TimeSpan GetNextDelayAfterLastFailure()
    {
        if (_failureCount == 0)
        {
            return TimeSpan.Zero;
        }

        return GetDelayAfterFailure(_failureCount);
    }
}