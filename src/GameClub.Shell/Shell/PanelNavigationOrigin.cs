using System;

namespace GameClub.Shell.Shell;

internal static class PanelNavigationOrigin
{
    public static bool IsSamePanelOrigin(Uri candidate, Uri allowedOrigin)
    {
        if (!string.IsNullOrEmpty(candidate.UserInfo))
        {
            return false;
        }

        if (!string.Equals(candidate.Scheme, allowedOrigin.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(candidate.Host, allowedOrigin.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return candidate.Port == allowedOrigin.Port;
    }

    public static bool IsAllowedPanelNavigationUri(string uriString)
    {
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var candidate))
        {
            return false;
        }

        if (!Uri.TryCreate(ShellPaths.ResolveAllowedPanelOrigin(), UriKind.Absolute, out var allowedOrigin))
        {
            return false;
        }

        return IsSamePanelOrigin(candidate, allowedOrigin);
    }

    public static bool TryGetPanelOrigin(out Uri origin)
    {
        return Uri.TryCreate(ShellPaths.ResolveAllowedPanelOrigin(), UriKind.Absolute, out origin!);
    }
}