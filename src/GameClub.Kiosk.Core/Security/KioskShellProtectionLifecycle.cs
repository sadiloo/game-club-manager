namespace GameClub.Kiosk.Core.Security;

/// <summary>Apply process-object ACL after guard startup and each shell launch/relaunch.</summary>
public sealed class KioskShellProtectionLifecycle
{
    private readonly IKioskProcessTerminateProtector _protector;
    private readonly Action<string> _reportFault;

    public KioskShellProtectionLifecycle(IKioskProcessTerminateProtector protector, Action<string> reportFault)
    {
        _protector = protector;
        _reportFault = reportFault;
    }

    public void ProtectGuardProcess(int guardProcessId) => ApplyOrReportFault(guardProcessId);

    public void ProtectShellAfterLaunch(int shellProcessId) => ApplyOrReportFault(shellProcessId);

    private void ApplyOrReportFault(int processId)
    {
        if (_protector.TryDenyTerminate(processId, out var win32))
        {
            return;
        }

        _reportFault($"guard.process_acl_failed pid={processId} win32={win32}");
    }
}
