using GameClub.Kiosk.Core.Security;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskFixR9GuardPolicyTests
{
    private static Dictionary<string, int?> CorrectSystem() =>
        KioskRegistryPolicyTemplate.SystemPolicies.ToDictionary(
            kvp => kvp.Key,
            kvp => (int?)kvp.Value,
            StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, int?> CorrectExplorer() =>
        KioskRegistryPolicyTemplate.ExplorerPolicies.ToDictionary(
            kvp => kvp.Key,
            kvp => (int?)kvp.Value,
            StringComparer.OrdinalIgnoreCase);

  [Fact]
    public void guard_disallow_run_tamper_true_when_child_key_missing()
    {
        var system = CorrectSystem();
        var explorer = CorrectExplorer();
        Assert.True(KioskRegistryPolicyTemplate.IsTampered(system, explorer, DisallowRunPolicySnapshot.MissingChild));
    }

    [Fact]
    public void guard_disallow_run_enable_dword_missing_is_tampered()
    {
        var system = CorrectSystem();
        var explorer = CorrectExplorer();
        explorer.Remove("DisallowRun");
        Assert.True(KioskRegistryPolicyTemplate.IsTampered(system, explorer, DisallowRunPolicySnapshot.MissingChild));
    }

    [Fact]
    public void guard_disallow_run_enable_dword_zero_is_tampered()
    {
        var system = CorrectSystem();
        var explorer = CorrectExplorer();
        explorer["DisallowRun"] = 0;
        var entries = KioskRegistryPolicyTemplate.ResolveDisallowRunEntries().ToList();
        Assert.True(KioskRegistryPolicyTemplate.IsTampered(system, explorer, DisallowRunPolicySnapshot.WithEntries(entries)));
    }

    [Fact]
    public void guard_disallow_run_correct_list_is_not_tampered()
    {
        var system = CorrectSystem();
        var explorer = CorrectExplorer();
        var entries = KioskRegistryPolicyTemplate.ResolveDisallowRunEntries().ToList();
        Assert.False(KioskRegistryPolicyTemplate.IsTampered(system, explorer, DisallowRunPolicySnapshot.WithEntries(entries)));
    }

    [Fact]
    public void guard_disallow_run_truncated_list_is_tampered()
    {
        var system = CorrectSystem();
        var explorer = CorrectExplorer();
        var truncated = KioskRegistryPolicyTemplate.ResolveDisallowRunEntries().Skip(1).ToList();
        Assert.True(KioskRegistryPolicyTemplate.IsTampered(system, explorer, DisallowRunPolicySnapshot.WithEntries(truncated)));
    }

    [Fact]
    public void guard_shell_protection_applies_acl_after_each_launch_pid()
    {
        var fake = new RecordingProtector();
        var faults = new List<string>();
        var lifecycle = new KioskShellProtectionLifecycle(fake, faults.Add);
        lifecycle.ProtectShellAfterLaunch(100);
        lifecycle.ProtectShellAfterLaunch(200);
        Assert.Equal([100, 200], fake.ProtectedPids);
        Assert.Empty(faults);
    }

    [Fact]
    public void guard_shell_protection_relaunch_new_pid_gets_second_acl_apply()
    {
        var fake = new RecordingProtector();
        var lifecycle = new KioskShellProtectionLifecycle(fake, _ => { });
        lifecycle.ProtectShellAfterLaunch(100);
        lifecycle.ProtectShellAfterLaunch(200);
        Assert.Equal(2, fake.CallCount);
        Assert.Equal(200, fake.ProtectedPids[^1]);
    }

    [Fact]
    public void guard_shell_protection_reports_fault_when_set_kernel_security_fails()
    {
        var fake = new RecordingProtector { FailPid = 42, Win32OnFail = 5 };
        var faults = new List<string>();
        var lifecycle = new KioskShellProtectionLifecycle(fake, faults.Add);
        lifecycle.ProtectShellAfterLaunch(42);
        Assert.Single(faults);
        Assert.Contains("guard.process_acl_failed pid=42 win32=5", faults[0], StringComparison.Ordinal);
    }

    [Fact]
    public void guard_shell_protection_applies_to_guard_pid_at_startup()
    {
        var fake = new RecordingProtector();
        var lifecycle = new KioskShellProtectionLifecycle(fake, _ => { });
        const int guardPid = 999;
        lifecycle.ProtectGuardProcess(guardPid);
        Assert.Single(fake.ProtectedPids);
        Assert.Equal(guardPid, fake.ProtectedPids[0]);
    }

    private sealed class RecordingProtector : IKioskProcessTerminateProtector
    {
        public List<int> ProtectedPids { get; } = [];

        public int CallCount { get; private set; }

        public int? FailPid { get; init; }

        public int Win32OnFail { get; init; }

        public bool TryDenyTerminate(int processId, out int win32Error)
        {
            CallCount++;
            ProtectedPids.Add(processId);
            if (FailPid == processId)
            {
                win32Error = Win32OnFail;
                return false;
            }

            win32Error = 0;
            return true;
        }
    }
}
