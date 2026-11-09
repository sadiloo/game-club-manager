using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace GameClub.Kiosk.Guard;

/// <summary>Deny the kiosk user terminate/stop rights on guard and kiosk shell processes (freeze L5).</summary>
internal static class KioskProcessAcl
{
    private const uint ProcessTerminate = 0x0001;
    private const int DaclSecurityInformation = 0x00000004;

    internal static void ApplyForProtectedProcesses()
    {
        var sidRaw = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_SID")?.Trim();
        if (string.IsNullOrWhiteSpace(sidRaw))
        {
            return;
        }

        var kioskSid = new SecurityIdentifier(sidRaw);
        DenyTerminateOnProcess(Process.GetCurrentProcess(), kioskSid);

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
                DenyTerminateOnProcess(process, kioskSid);
            }
            catch
            {
                // best-effort when shell is not running yet
            }
        }
    }

    private static void DenyTerminateOnProcess(Process process, SecurityIdentifier kioskSid)
    {
        var handle = process.Handle;
        if (!GetKernelObjectSecurity(handle, DaclSecurityInformation, IntPtr.Zero, 0, out var length)
            && Marshal.GetLastWin32Error() != 122)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(Math.Max((int)length, 512));
        try
        {
            if (!GetKernelObjectSecurity(handle, DaclSecurityInformation, buffer, length, out _))
            {
                return;
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
                SetKernelObjectSecurity(handle, DaclSecurityInformation, writePtr);
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
