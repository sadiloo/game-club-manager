using System;

namespace GameClub.Shell.Shell;

internal static class RunningStabilityPolicy
{
    public static readonly TimeSpan StableRunningRequired = TimeSpan.FromSeconds(60);
}

internal sealed class RunningStabilityTracker
{
    private DateTime? _runningSinceUtc;

    public void Observe(ShellServerVisualState state, DateTime utcNow)
    {
        if (state == ShellServerVisualState.Running)
        {
            _runningSinceUtc ??= utcNow;
        }
        else
        {
            _runningSinceUtc = null;
        }
    }

    public bool IsRunningStable(DateTime utcNow)
    {
        return _runningSinceUtc.HasValue
            && utcNow >= _runningSinceUtc.Value + RunningStabilityPolicy.StableRunningRequired;
    }

    public DateTime? RunningSinceUtc => _runningSinceUtc;

    public void Reset()
    {
        _runningSinceUtc = null;
    }
}