using System;
using System.Collections.Generic;

namespace GameClub.Shell.Shell;

internal sealed class PanelNavigationPendingRegistry
{
    private readonly Dictionary<ulong, (string Uri, DateTime StartedUtc)> _pendingStartUris = new();

    public int PendingCount => _pendingStartUris.Count;

    public void RecordNavigationStarting(ulong navigationId, string uri, DateTime startedUtc)
    {
        _pendingStartUris[navigationId] = (uri, startedUtc);
    }

    public bool TryTakeNavigationCompleted(ulong navigationId, out string startUri)
    {
        if (_pendingStartUris.TryGetValue(navigationId, out var entry))
        {
            _pendingStartUris.Remove(navigationId);
            startUri = entry.Uri;
            return true;
        }

        startUri = "";
        return false;
    }

    public bool TryGetOldestPendingLoopbackPanelNavigation(
        Func<string, bool> isLoopbackPanelTarget,
        DateTime utcNow,
        out TimeSpan age,
        out ulong navigationId)
    {
        age = TimeSpan.Zero;
        navigationId = 0;
        var found = false;
        DateTime oldestStart = DateTime.MaxValue;
        foreach (var pending in _pendingStartUris)
        {
            var uri = pending.Value.Uri;
            var startedUtc = pending.Value.StartedUtc;
            if (!isLoopbackPanelTarget(uri))
            {
                continue;
            }

            if (startedUtc < oldestStart)
            {
                oldestStart = startedUtc;
                navigationId = pending.Key;
                found = true;
            }
        }

        if (!found)
        {
            return false;
        }

        age = utcNow - oldestStart;
        return true;
    }

    public bool TryRemovePendingLoopbackNavigation(ulong navigationId)
    {
        return _pendingStartUris.Remove(navigationId);
    }
}