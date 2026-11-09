using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using GameClub.Kiosk.Core.Security;

namespace GameClub.Kiosk.Guard;

/// <summary>Deny the kiosk user terminate/stop rights on guard and kiosk shell processes (freeze L5).</summary>
internal sealed class KioskProcessTerminateProtector : IKioskProcessTerminateProtector
{
    private const uint ProcessTerminate = 0x0001;
    private const int DaclSecurityInformation = 0x00000004;

    public bool TryDenyTerminate(int processId, out int win32Error)
    {
        win32Error = 0;
        var sidRaw = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_SID")?.Trim();
        if (string.IsNullOrWhiteSpace(sidRaw))
        {
            win32Error = 87;
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return TryDenyTerminateOnProcess(process, new SecurityIdentifier(sidRaw), out win32Error);
        }
        catch
        {
            win32Error = Marshal.GetLastWin32Error();
            if (win32Error == 0)
            {
                win32Error = 87;
            }

            return false;
        }
    }

    internal static void ApplyStartupProtection(KioskShellProtectionLifecycle lifecycle)
    {
        lifecycle.ProtectGuardProcess(Process.GetCurrentProcess().Id);

        var kioskExe = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_EXE");
        if (string.IsNullOrWhiteSpace(kioskExe))
        {
            return;
        }

        var exeName = Path.GetFileNameWithoutExtension(kioskExe);
        foreach (var process in Process.GetProcessesByName(exeName))
        {
            try
            {
                lifecycle.ProtectShellAfterLaunch(process.Id);
            }
            catch
            {
                // best-effort when shell is not running yet
            }
        }
    }

    private static bool TryDenyTerminateOnProcess(Process process, SecurityIdentifier kioskSid, out int win32Error)
    {
        win32Error = 0;
        var handle = process.Handle;
        if (!GetKernelObjectSecurity(handle, DaclSecurityInformation, IntPtr.Zero, 0, out var length))
        {
            win32Error = Marshal.GetLastWin32Error();
            if (win32Error != 122)
            {
                return false;
            }
        }

        var buffer = Marshal.AllocHGlobal(Math.Max((int)length, 512));
        try
        {
            if (!GetKernelObjectSecurity(handle, DaclSecurityInformation, buffer, length, out _))
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }

            var sdBytes = new byte[length];
            Marshal.Copy(buffer, sdBytes, 0, (int)length);
            var sd = new RawSecurityDescriptor(sdBytes, 0);
            var dacl = sd.DiscretionaryAcl ?? new RawAcl(2, 1);
            dacl.InsertAce(
                0,
                new CommonAce(AceFlags.None, AceQualifier.AccessDenied, (int)ProcessTerminate, kioskSid, false, null));
            sd.DiscretionaryAcl = dacl;
            var form = new byte[sd.BinaryLength];
            sd.GetBinaryForm(form, 0);
            var writePtr = Marshal.AllocHGlobal(form.Length);
            try
            {
                Marshal.Copy(form, 0, writePtr, form.Length);
                if (!SetKernelObjectSecurity(handle, DaclSecurityInformation, writePtr))
                {
                    win32Error = Marshal.GetLastWin32Error();
                    return false;
                }

                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(writePtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetKernelObjectSecurity(
        IntPtr handle,
        int securityInformation,
        IntPtr securityDescriptor,
        uint length,
        out uint lengthNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(IntPtr handle, int securityInformation, IntPtr securityDescriptor);
}
