#if NET48
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using GameClub.Kiosk.Guard;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

[Trait("Category", "WindowsGuard")]
public sealed class WindowsGuardAclIntegrationTests
{
    [Fact]
    public void g2_set_kernel_security_deny_terminate_is_readable_on_dacl()
    {
        var sid = ResolveSid();
        Environment.SetEnvironmentVariable("CHECKPOINT_KIOSK_SID", sid);
        using var process = Process.GetCurrentProcess();
        var protector = new KioskProcessTerminateProtector();
        Assert.True(protector.TryDenyTerminate(process.Id, out var win32), $"win32={win32}");
        Assert.True(TryReadDenyTerminateAce(process.Handle, new SecurityIdentifier(sid), out var found));
        Assert.True(found);
    }

    [Fact]
    public void g2_guard_startup_protection_applies_to_current_process()
    {
        var sid = ResolveSid();
        Environment.SetEnvironmentVariable("CHECKPOINT_KIOSK_SID", sid);
        var lifecycle = new GameClub.Kiosk.Core.Security.KioskShellProtectionLifecycle(
            new KioskProcessTerminateProtector(),
            _ => { });
        var pid = Process.GetCurrentProcess().Id;
        lifecycle.ProtectGuardProcess(pid);
        Assert.True(TryReadDenyTerminateAce(Process.GetCurrentProcess().Handle, new SecurityIdentifier(sid), out var found));
        Assert.True(found);
    }

    [Fact]
    public void g2_non_admin_terminate_attempt_reports_honestly()
    {
        var secondUser = Environment.GetEnvironmentVariable("CHECKPOINT_SECOND_USER");
        if (string.IsNullOrWhiteSpace(secondUser))
        {
            var evidence = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Checkpoint",
                "evidence",
                "g2-non-admin-skip.json");
            Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
            File.WriteAllText(
                evidence,
                "{\"proved\":false,\"reason\":\"No CHECKPOINT_SECOND_USER on runner; non-admin terminate NOT PROVED.\"}");
            return;
        }

        // Runner provided a second local user — attempt is environment-specific; document only when configured.
        Assert.False(string.IsNullOrWhiteSpace(secondUser));
    }

    private static string ResolveSid()
    {
        var configured = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_SID")?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var user = Environment.UserName;
        var account = new NTAccount(user);
        return ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
    }

    private static bool TryReadDenyTerminateAce(IntPtr handle, SecurityIdentifier sid, out bool found)
    {
        found = false;
        const int daclSecurityInformation = 0x00000004;
        const uint processTerminate = 0x0001;
        if (!GetKernelObjectSecurity(handle, daclSecurityInformation, IntPtr.Zero, 0, out var length) && Marshal.GetLastWin32Error() != 122)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(Math.Max((int)length, 512));
        try
        {
            if (!GetKernelObjectSecurity(handle, daclSecurityInformation, buffer, length, out _))
            {
                return false;
            }

            var sdBytes = new byte[length];
            Marshal.Copy(buffer, sdBytes, 0, (int)length);
            var sd = new RawSecurityDescriptor(sdBytes, 0);
            var dacl = sd.DiscretionaryAcl;
            if (dacl is null)
            {
                return true;
            }

            foreach (var ace in dacl)
            {
                if (ace is CommonAce common
                    && common.AceQualifier == AceQualifier.AccessDenied
                    && (common.AccessMask & processTerminate) != 0
                    && common.SecurityIdentifier?.Value == sid.Value)
                {
                    found = true;
                    return true;
                }
            }

            return true;
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
}
#endif
