namespace GameClub.Kiosk.Core;

/// <summary>
/// The guard runs in session 0 as SYSTEM. A process it starts with Process.Start
/// stays there and the kiosk user never sees it. The shell counts as running only
/// when it is in the active console session and the token user is the kiosk SID.
/// </summary>
public static class InteractiveSession
{
    public const string Desktop = @"winsta0\default";

    public const string ShellMutexName = @"Local\GameClub.Kiosk";

    public static bool IsKioskShell(int processSessionId, int activeConsoleSessionId, string? processUserSid, string? kioskUserSid)
    {
        if (processSessionId <= 0 || activeConsoleSessionId <= 0)
        {
            return false;
        }

        if (processSessionId != activeConsoleSessionId)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(processUserSid) || string.IsNullOrWhiteSpace(kioskUserSid))
        {
            return false;
        }

        return string.Equals(processUserSid!.Trim(), kioskUserSid!.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldKillBeforeRestart(bool hung) => hung;
}
