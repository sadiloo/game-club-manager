namespace GameClub.Kiosk.Core.Security;

/// <summary>Deny terminate rights on a process object (Windows implementation in Guard).</summary>
public interface IKioskProcessTerminateProtector
{
    bool TryDenyTerminate(int processId, out int win32Error);
}
