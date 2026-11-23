using System;

namespace GameClub.Shell.Shell;

internal static class RunningStabilityPolicy
{
    public static readonly TimeSpan StableRunningRequired = TimeSpan.FromSeconds(60);
}

internal sealed class RunningStabilityTracker
{
    private DateTime? _runningSinceUtc;
    private bool _stableResetEdgeArmed = true;

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
        _stableResetEdgeArmed = true;
    }

    /// <summary>
    /// Returns true once per stable-running episode (first tick at or after 60s continuous Running).
    /// Re-arms when Running is lost.
    /// </summary>
    public bool TryConsumeStableRunningResetEdge(DateTime utcNow)
    {
        if (!IsRunningStable(utcNow))
        {
            _stableResetEdgeArmed = true;
            return false;
        }

        if (!_stableResetEdgeArmed)
        {
            return false;
        }

        _stableResetEdgeArmed = false;
        return true;
    }
}