#if NET48
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Kiosk.Core.Security;
using GameClub.Kiosk.Guard;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

[Collection(WindowsGuardIntegrationCollection.Name)]
[Trait("Category", "WindowsGuard")]
public sealed class WindowsGuardRuntimeIntegrationTests
{
    [Fact]
    public void allowlist_terminates_real_denied_process_and_preserves_allowed()
    {
        Process cmd = null;
        Process deniedCopy = null;
        Process notepad = null;
        var deniedCopyPath = (string)null;
        try
        {
            cmd = Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true });
            Assert.NotNull(cmd);
            Thread.Sleep(500);
            Assert.False(cmd.HasExited, "cmd.exe must be alive before deny-list enforcement.");

            new KioskShellOrchestrator().KillDeniedProcesses();
            var denyDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < denyDeadline && !cmd.HasExited)
            {
                Thread.Sleep(200);
            }

            Assert.True(cmd.HasExited, "Production deny-list enforcement must terminate cmd.exe within 10s.");

            var pingSrc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
            deniedCopyPath = Path.Combine(Path.GetTempPath(), $"notallowed_{Guid.NewGuid():N}.exe");
            File.Copy(pingSrc, deniedCopyPath);
            deniedCopy = Process.Start(new ProcessStartInfo(deniedCopyPath, "-t 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true });
            Assert.NotNull(deniedCopy);
            Thread.Sleep(500);
            Assert.False(deniedCopy.HasExited, "Copied denied executable must be alive before allow-list enforcement.");

            new KioskShellOrchestrator().EnforceAllowlist(new[] { "onlygame" });
            var allowDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < allowDeadline && !deniedCopy.HasExited)
            {
                Thread.Sleep(200);
            }

            Assert.True(deniedCopy.HasExited, "Allow-list enforcement must terminate non-allowlisted copied exe within 10s.");

            notepad = Process.Start("notepad.exe");
            Assert.NotNull(notepad);
            Thread.Sleep(300);
            Assert.False(notepad.HasExited, "notepad.exe must be alive before allow-list preservation check.");

            new KioskShellOrchestrator().EnforceAllowlist(new[] { "notepad" });
            Thread.Sleep(TimeSpan.FromSeconds(10));
            Assert.False(notepad.HasExited, "Allow-listed notepad.exe must still be running 10s after enforcement.");
        }
        finally
        {
            try { cmd?.Kill(); } catch { }
            try { deniedCopy?.Kill(); } catch { }
            try { notepad?.Kill(); } catch { }
            if (!string.IsNullOrEmpty(deniedCopyPath))
            {
                try { File.Delete(deniedCopyPath); } catch { }
            }
        }
    }

    [Fact]
    public void session_end_clears_launcher_cache_paths()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);
        var seed = Path.Combine(downloads, "checkpoint-r11-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(seed, "x");
        var deleteTree = typeof(GameClub.Kiosk.Services.KioskHostService).GetMethod(
            "DeleteTreeContents",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(deleteTree);
        try
        {
            deleteTree!.Invoke(null, new object[] { downloads });
            Assert.False(File.Exists(seed));
        }
        finally
        {
            try { if (File.Exists(seed)) File.Delete(seed); } catch { }
        }
    }

    [Fact]
    public void guard_watchdog_relaunches_killed_shell_logged()
    {
        var sid = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_SID") ?? ResolveSid();
        var kioskExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameClub.Kiosk.exe");
        if (!File.Exists(kioskExe))
        {
            Assert.Fail("GameClub.Kiosk.exe is required for watchdog relaunch proof.");
        }

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Checkpoint",
            "guard",
            "watchdog.log");
        if (File.Exists(logPath))
        {
            File.Delete(logPath);
        }

        using var guard = GuardProcessStart.StartGuard(kioskExe);
        Process shell = null;
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline && shell == null)
        {
            shell = Process.GetProcessesByName("GameClub.Kiosk").FirstOrDefault();
            Thread.Sleep(500);
        }

        if (shell == null)
        {
            guard.Kill();
            Assert.Fail("Guard did not launch GameClub.Kiosk within 45s (runner session may lack interactive kiosk user).");
        }

        var shellPid = shell.Id;
        var killTimeUtc = DateTime.UtcNow;
        shell.Kill();
        var restartDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < restartDeadline)
        {
            if (!File.Exists(logPath))
            {
                Thread.Sleep(500);
                continue;
            }

            foreach (var raw in File.ReadAllLines(logPath))
            {
                if (!raw.Contains("guard.restart", StringComparison.Ordinal) || !raw.Contains("launched_pid_", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !DateTime.TryParse(parts[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out var stamp))
                {
                    continue;
                }

                if (stamp.ToUniversalTime() <= killTimeUtc)
                {
                    continue;
                }

                var launched = parts[2];
                const string prefix = "launched_pid_";
                if (!launched.StartsWith(prefix, StringComparison.Ordinal)
                    || !int.TryParse(launched.Substring(prefix.Length), out var newPid)
                    || newPid == shellPid)
                {
                    continue;
                }

                Process relaunched;
                try
                {
                    relaunched = Process.GetProcessById(newPid);
                }
                catch
                {
                    continue;
                }

                Assert.False(relaunched.HasExited, "Relaunched kiosk PID must be alive.");
                Assert.True(TryReadDenyTerminateAce(relaunched.Handle, new SecurityIdentifier(sid), out var found));
                Assert.True(found);
                relaunched.Kill();
                guard.Kill();
                return;
            }

            Thread.Sleep(500);
        }

        guard.Kill();
        Assert.Fail("Guard did not log guard.restart with ISO timestamp after shell kill and a new live PID within 15s.");
    }

    [Fact]
    public void g2_acl_on_guard_and_supervised_shell_after_launch()
    {
        var sid = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_SID") ?? ResolveSid();
        var kioskExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameClub.Kiosk.exe");
        Assert.True(File.Exists(kioskExe));
        using var guard = GuardProcessStart.StartGuard(kioskExe);
        Thread.Sleep(3000);
        Assert.True(TryReadDenyTerminateAce(guard.Handle, new SecurityIdentifier(sid), out var guardAce));
        Assert.True(guardAce);
        var shell = Process.GetProcessesByName("GameClub.Kiosk").FirstOrDefault();
        if (shell == null)
        {
            guard.Kill();
            Assert.Fail("Supervised shell PID not found after Guard launch.");
        }

        Assert.True(TryReadDenyTerminateAce(shell.Handle, new SecurityIdentifier(sid), out var shellAce));
        Assert.True(shellAce);
        shell.Kill();
        guard.Kill();
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
            if (dacl == null)
            {
                return true;
            }

            foreach (var ace in dacl)
            {
                if (ace is CommonAce common
                    && common.AceQualifier == AceQualifier.AccessDenied
                    && (common.AccessMask & processTerminate) != 0
                    && common.SecurityIdentifier != null
                    && common.SecurityIdentifier.Value == sid.Value)
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

    private static string ResolveSid()
    {
        var account = new NTAccount(Environment.UserName);
        return ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
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
