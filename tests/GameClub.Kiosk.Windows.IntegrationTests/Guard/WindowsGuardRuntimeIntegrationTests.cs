#if NET48
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using GameClub.Kiosk.Core.Security;
using GameClub.Kiosk.Guard;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

[Trait("Category", "WindowsGuard")]
public sealed class WindowsGuardRuntimeIntegrationTests
{
    [Fact]
    public void allowlist_blocks_cmd_allows_configured_game_process()
    {
        var enforcer = new ProcessPolicyEnforcer();
        Assert.False(enforcer.IsProcessAllowed("cmd", new[] { "notepad" }));
        Assert.True(enforcer.IsProcessAllowed("notepad", new[] { "notepad" }));
    }

    [Fact]
    public void session_end_clears_launcher_cache_paths()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);
        var seed = Path.Combine(downloads, "checkpoint-r11-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(seed, "x");
        var method = typeof(GameClub.Kiosk.Services.KioskHostService).GetMethod(
            "ClearKioskProfileScratch",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        try
        {
            method!.Invoke(null, null);
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

        var guardExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameClub.Kiosk.Guard.exe");
        var psi = new ProcessStartInfo(guardExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(guardExe),
        };
        psi.EnvironmentVariables["CHECKPOINT_KIOSK_SID"] = sid;
        psi.EnvironmentVariables["CHECKPOINT_KIOSK_USER"] = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_USER") ?? Environment.UserName;
        psi.EnvironmentVariables["CHECKPOINT_KIOSK_EXE"] = kioskExe;
        using var guard = Process.Start(psi);
        Assert.NotNull(guard);

        Process? shell = null;
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline && shell is null)
        {
            shell = Process.GetProcessesByName("GameClub.Kiosk").FirstOrDefault();
            Thread.Sleep(500);
        }

        if (shell is null)
        {
            guard!.Kill(entireProcessTree: true);
            Assert.Fail("Guard did not launch GameClub.Kiosk within 45s (runner session may lack interactive kiosk user).");
        }

        var shellPid = shell!.Id;
        shell.Kill();
        var restartDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < restartDeadline)
        {
            if (File.Exists(logPath) && File.ReadAllText(logPath).Contains("guard.restart", StringComparison.Ordinal))
            {
                var relaunched = Process.GetProcessesByName("GameClub.Kiosk").FirstOrDefault(p => p.Id != shellPid);
                if (relaunched is not null)
                {
                    Assert.True(TryReadDenyTerminate(relaunched.Id, sid));
                    relaunched.Kill();
                }

                guard!.Kill(entireProcessTree: true);
                return;
            }

            Thread.Sleep(500);
        }

        guard!.Kill(entireProcessTree: true);
        Assert.Fail("Guard did not log guard.restart after shell kill within 15s.");
    }

  [Fact]
    public void g2_acl_on_guard_and_supervised_shell_after_launch()
    {
        var sid = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_SID") ?? ResolveSid();
        var kioskExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameClub.Kiosk.exe");
        Assert.True(File.Exists(kioskExe));
        var guardExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameClub.Kiosk.Guard.exe");
        var psi = new ProcessStartInfo(guardExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.EnvironmentVariables["CHECKPOINT_KIOSK_SID"] = sid;
        psi.EnvironmentVariables["CHECKPOINT_KIOSK_USER"] = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_USER") ?? Environment.UserName;
        psi.EnvironmentVariables["CHECKPOINT_KIOSK_EXE"] = kioskExe;
        using var guard = Process.Start(psi);
        Assert.NotNull(guard);
        Thread.Sleep(3000);
        Assert.True(TryReadDenyTerminate(guard!.Id, sid));
        var shell = Process.GetProcessesByName("GameClub.Kiosk").FirstOrDefault();
        if (shell is null)
        {
            guard.Kill(entireProcessTree: true);
            Assert.Fail("Supervised shell PID not found after Guard launch.");
        }

        Assert.True(TryReadDenyTerminate(shell.Id, sid));
        shell.Kill();
        guard.Kill(entireProcessTree: true);
    }

    private static bool TryReadDenyTerminate(int pid, string sid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var handle = process.Handle;
            const int daclSecurityInformation = 0x00000004;
            const uint processTerminate = 0x0001;
            var sd = process.GetAccessControl();
            foreach (KnownAce ace in sd.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (ace is CommonAce common
                    && common.AceQualifier == AceQualifier.AccessDenied
                    && (common.AccessMask & processTerminate) != 0
                    && common.SecurityIdentifier?.Value == sid)
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string ResolveSid()
    {
        var account = new NTAccount(Environment.UserName);
        return ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
    }
}
#endif
