namespace GameClub.Kiosk.Guard;

internal static class GuardWatchdog
{
    internal static void Run(CancellationToken ct)
    {
        var identity = KioskGuardIdentity.Resolve();
        if (!identity.IsResolved)
        {
            return;
        }

        new KioskShellSupervisor(identity).Run(ct);
    }
}
