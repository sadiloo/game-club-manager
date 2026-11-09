using GameClub.Kiosk.Core.Security;

namespace GameClub.Kiosk.Guard;

/// <summary>Public ACL helper for kiosk shell and guard process protection (shipped; used by tests via subprocess and DACL read-back).</summary>
public sealed class GuardProcessProtection : IKioskProcessTerminateProtector
{
    private readonly KioskProcessTerminateProtector _inner = new();

    public bool TryDenyTerminate(int processId, out int win32Error) => _inner.TryDenyTerminate(processId, out win32Error);

    public static void ApplyStartupProtection(KioskShellProtectionLifecycle lifecycle)
        => KioskProcessTerminateProtector.ApplyStartupProtection(lifecycle);
}
