using System.Collections.Generic;

namespace GameClub.Shell.Shell;

internal sealed class PanelNavigationPendingRegistry
{
    private readonly Dictionary<ulong, string> _pendingStartUris = new();

    public int PendingCount => _pendingStartUris.Count;

    public void RecordNavigationStarting(ulong navigationId, string uri)
    {
        _pendingStartUris[navigationId] = uri;
    }

    public bool TryTakeNavigationCompleted(ulong navigationId, out string startUri)
    {
        if (_pendingStartUris.TryGetValue(navigationId, out startUri!))
        {
            _pendingStartUris.Remove(navigationId);
            return true;
        }

        startUri = "";
        return false;
    }
}