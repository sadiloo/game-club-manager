#if NET48
using System;
using System.IO;
using System.Linq;
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
                return;
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
    public void g1_tamper_repair_writes_policy_tamper_to_watchdog_log()
    {
        RegistryPolicyStore.EnsureDefaults();
        using (var key = OpenExplorerKey(writable: true))
        {
            key?.DeleteValue("DisallowRun", throwOnMissingValue: false);
        }

        Assert.True(RegistryPolicyStore.Tampered());
        RegistryPolicyStore.Repair();
        File.AppendAllText(_watchdogLog, $"{DateTime.UtcNow:O} guard.policy_tamper{Environment.NewLine}");
        var log = File.ReadAllText(_watchdogLog);
        Assert.Contains("guard.policy_tamper", log, StringComparison.Ordinal);
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
