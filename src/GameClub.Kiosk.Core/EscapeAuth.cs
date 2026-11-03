namespace GameClub.Kiosk.Core;

/// <summary>
/// Admin escape is decided by the guard, not by the shell. The shell only forwards
/// the password. The client process image must be the installed shell, and the
/// password must match the PBKDF2 hash.
/// </summary>
public static class EscapeAuth
{
    public static bool TryAuthorize(
        AdminGate? gate,
        string? password,
        string? clientImagePath,
        string? installedShellPath,
        DateTimeOffset now,
        TimeSpan window,
        out string reason)
    {
        if (gate is null)
        {
            reason = "no_admin";
            return false;
        }

        if (!ImageMatches(clientImagePath, installedShellPath))
        {
            reason = "unexpected_process";
            return false;
        }

        if (!gate.TryOpen(password ?? "", now, window))
        {
            reason = "rejected";
            return false;
        }

        reason = "ok";
        return true;
    }

    public static bool ImageMatches(string? clientImagePath, string? installedShellPath)
    {
        if (!LaunchPolicy.TryNormalize(clientImagePath ?? "", out var client))
        {
            return false;
        }

        if (!LaunchPolicy.TryNormalize(installedShellPath ?? "", out var shell))
        {
            return false;
        }

        return string.Equals(client, shell, StringComparison.OrdinalIgnoreCase);
    }
}
