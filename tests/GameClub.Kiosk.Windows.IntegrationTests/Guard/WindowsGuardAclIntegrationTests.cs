#if NET48
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Security.AccessControl;
using System.Security.Principal;
using GameClub.Kiosk.Guard;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

[Collection(WindowsGuardIntegrationCollection.Name)]
[Trait("Category", "WindowsGuard")]
public sealed class WindowsGuardAclIntegrationTests
{
    [Fact(Timeout = 120_000)]
    public void g2_set_kernel_security_deny_terminate_is_readable_on_dacl()
    {
        var sid = ResolveSid();
        Environment.SetEnvironmentVariable("CHECKPOINT_KIOSK_SID", sid);
        using var process = Process.GetCurrentProcess();
        var protector = new GuardProcessProtection();
        Assert.True(protector.TryDenyTerminate(process.Id, out var win32), $"win32={win32}");
        Assert.True(TryReadDenyTerminateAce(process.Handle, new SecurityIdentifier(sid), out var found));
        Assert.True(found);
    }

    [Fact(Timeout = 120_000)]
    public void g2_guard_startup_protection_applies_to_launched_guard_process()
    {
        var sid = ResolveSid();
        Environment.SetEnvironmentVariable("CHECKPOINT_KIOSK_SID", sid);
        using var guard = GuardProcessStart.StartGuard();
        Thread.Sleep(2000);
        Assert.True(TryReadDenyTerminateAce(guard!.Handle, new SecurityIdentifier(sid), out var found));
        Assert.True(found);
        guard.Kill();
    }

    [Fact(Timeout = 120_000)]
    public void g2_non_admin_terminate_attempt_reports_honestly()
    {
        var secondUser = Environment.GetEnvironmentVariable("CHECKPOINT_SECOND_USER")?.Trim();
        var secondPassword = Environment.GetEnvironmentVariable("CHECKPOINT_SECOND_PASSWORD")?.Trim();
        if (string.IsNullOrWhiteSpace(secondUser) || string.IsNullOrWhiteSpace(secondPassword))
        {
            Assert.Fail("CHECKPOINT_SECOND_USER and CHECKPOINT_SECOND_PASSWORD must be set on windows-2022 kiosk integration.");
        }

        var sid = ResolveSid();
        Environment.SetEnvironmentVariable("CHECKPOINT_KIOSK_SID", sid);
        var protector = new GuardProcessProtection();
        using var current = Process.GetCurrentProcess();
        Assert.True(protector.TryDenyTerminate(current.Id, out _));

        var cred = new System.Net.NetworkCredential(secondUser, secondPassword, ".");
        var result = RunAsDenyTerminateProbe(cred, current.Id);
        Assert.True(result, "Expected non-admin terminate attempt to be denied on DACL-protected process.");
    }

    private static bool RunAsDenyTerminateProbe(System.Net.NetworkCredential cred, int pid)
    {
        var script = Path.Combine(Path.GetTempPath(), $"g2-probe-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(
            script,
            $"""
            $p = Get-Process -Id {pid} -ErrorAction Stop
            Exit 1
            """);
        try
        {
            var psi = new ProcessStartInfo(
                "powershell.exe",
                $"-NoProfile -NonInteractive -Command \"try {{ Stop-Process -Id {pid} -Force -ErrorAction Stop; exit 0 }} catch {{ exit 2 }}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Domain = cred.Domain,
                UserName = cred.UserName,
                Password = ToSecureString(cred.Password),
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(15000);
            return proc is { ExitCode: 2 or not 0 };
        }
        finally
        {
            try { File.Delete(script); } catch { }
        }
    }

    private static System.Security.SecureString ToSecureString(string password)
    {
        var secure = new System.Security.SecureString();
        foreach (var c in password)
        {
            secure.AppendChar(c);
        }

        secure.MakeReadOnly();
        return secure;
    }

    private static string ResolveGuardExePath()
    {
        var probe = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".", "GameClub.Kiosk.Guard.exe");
        if (File.Exists(probe))
        {
            return probe;
        }

        probe = Path.Combine(AppContext.BaseDirectory, "GameClub.Kiosk.Guard.exe");
        Assert.True(File.Exists(probe), "GameClub.Kiosk.Guard.exe not found beside test output.");
        return probe;
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
