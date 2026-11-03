namespace GameClub.Kiosk.Core;

/// <summary>
/// Decisions the guard loop applies to a pipe frame. Escape and exit are refused
/// until <see cref="EscapeAuth"/> accepts the password and the caller image.
/// </summary>
public static class GuardCommands
{
    public static bool TryAuthorizePrivileged(
        PipeMessage message,
        string? clientImagePath,
        string? installedShellPath,
        AdminGate? gate,
        DateTimeOffset now,
        TimeSpan window,
        out DateTimeOffset? openUntil,
        out string reason)
    {
        openUntil = null;
        var kind = message.Kind ?? "";
        var privileged = string.Equals(kind, "admin-escape", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "exit", StringComparison.OrdinalIgnoreCase);
        if (!privileged)
        {
            reason = "not_privileged";
            return false;
        }

        if (!EscapeAuth.TryAuthorize(gate, message.Password, clientImagePath, installedShellPath, now, window, out reason))
        {
            return false;
        }

        if (string.Equals(kind, "admin-escape", StringComparison.OrdinalIgnoreCase))
        {
            openUntil = now.Add(window);
        }

        reason = string.Equals(kind, "exit", StringComparison.OrdinalIgnoreCase) ? "exit" : "ok";
        return true;
    }
}
