#if NET48
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using GameClub.Kiosk.Core.Security;
using GameClub.Kiosk.Guard;
using Microsoft.Win32;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

/// <summary>Real HKCU/Users hive policy checks (G1 a–e on windows-2022).</summary>
[Trait("Category", "WindowsGuard")]
public sealed class WindowsGuardRegistryIntegrationTests : IDisposable
{
    private readonly string _sid;
    private readonly string _watchdogLog;

    public WindowsGuardRegistryIntegrationTests()
    {
        _sid = ResolveKioskSid();
        Environment.SetEnvironmentVariable("CHECKPOINT_KIOSK_SID", _sid);
        _watchdogLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Checkpoint",
            "guard",
            "watchdog.log");
        Directory.CreateDirectory(Path.GetDirectoryName(_watchdogLog)!);
        RegistryPolicyStore.EnsureDefaults();
    }

    public void Dispose() => RegistryPolicyStore.EnsureDefaults();

    [Fact]
    public void g1_defaults_are_not_tampered()
    {
        Assert.False(RegistryPolicyStore.Tampered());
    }

    [Fact]
    public void g1_missing_disallow_run_child_is_tampered()
    {
        DeleteDisallowRunHive();
        Assert.True(RegistryPolicyStore.Tampered());
        RegistryPolicyStore.Repair();
        Assert.False(RegistryPolicyStore.Tampered());
    }

    [Fact]
    public void g1_disallow_run_enable_zero_is_tampered()
    {
        RegistryPolicyStore.EnsureDefaults();
        using (var key = OpenExplorerKey(writable: true))
        {
            key?.SetValue("DisallowRun", 0, RegistryValueKind.DWord);
        }

        Assert.True(RegistryPolicyStore.Tampered());
    }

    [Fact]
    public void g1_truncated_disallow_list_is_tampered()
    {
        RegistryPolicyStore.EnsureDefaults();
        var expected = KioskRegistryPolicyTemplate.ResolveDisallowRunEntries().ToList();
        using (var key = OpenDisallowKey(writable: true))
        {
            if (key is null)
            {
                Assert.Fail("DisallowRun hive key is not available on this runner.");
            }

            foreach (var name in key.GetValueNames())
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }

            for (var i = 1; i < expected.Count; i++)
            {
                key.SetValue(i.ToString(), expected[i], RegistryValueKind.String);
            }
        }

        Assert.True(RegistryPolicyStore.Tampered());
    }

    [Fact]
    public void g1_tamper_triggers_guard_policy_tamper_log_line()
    {
        if (File.Exists(_watchdogLog))
        {
            File.Delete(_watchdogLog);
        }

        using var guard = GuardProcessStart.StartGuard();
        RegistryPolicyStore.EnsureDefaults();
        using (var key = OpenExplorerKey(writable: true))
        {
            key?.DeleteValue("DisallowRun", throwOnMissingValue: false);
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(_watchdogLog))
            {
                var log = File.ReadAllText(_watchdogLog);
                if (log.Contains("guard.policy_tamper", StringComparison.Ordinal))
                {
                    guard!.Kill();
                    return;
                }
            }

            Thread.Sleep(500);
        }

        guard!.Kill();
        Assert.Fail("Guard did not write guard.policy_tamper to watchdog.log after registry tamper.");
    }

    private static string ResolveGuardExePath()
    {
        var probe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameClub.Kiosk.Guard.exe");
        if (!File.Exists(probe))
        {
            throw new InvalidOperationException("GameClub.Kiosk.Guard.exe not found beside test output.");
        }

        return probe;
    }

    private string ResolveKioskSid()
    {
        var configured = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_SID")?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var user = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_USER") ?? Environment.UserName;
        var account = new System.Security.Principal.NTAccount(user);
        var sid = (System.Security.Principal.SecurityIdentifier)account.Translate(typeof(System.Security.Principal.SecurityIdentifier));
        return sid.Value;
    }

    private void DeleteDisallowRunHive()
    {
        var path = $"{_sid}\\{KioskRegistryPolicyTemplate.DisallowRunRelativePath}";
        try
        {
            Registry.Users.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch
        {
            // hive may not exist yet
        }
    }

    private RegistryKey OpenExplorerKey(bool writable)
    {
        var path = $"{_sid}\\{KioskRegistryPolicyTemplate.ExplorerPoliciesRelativePath}";
        return writable ? Registry.Users.CreateSubKey(path, true) : Registry.Users.OpenSubKey(path, writable: false);
    }

    private RegistryKey OpenDisallowKey(bool writable)
    {
        var path = $"{_sid}\\{KioskRegistryPolicyTemplate.DisallowRunRelativePath}";
        return writable ? Registry.Users.CreateSubKey(path, true) : Registry.Users.OpenSubKey(path, writable: false);
    }
}
#endif
