using GameClub.Kiosk.Core.Security;

namespace GameClub.Kiosk.Core.Tests;

/// <summary>
/// R10j (BL-rr8-1): the Guard watchdog wrote kiosk policies with <c>Registry.Users.CreateSubKey("&lt;sid&gt;\\...")</c>
/// before the kiosk hive was loaded. Windows cannot create a direct child of HKEY_USERS, so the write threw on the
/// watchdog thread and the CheckpointGuard service terminated right after Start-Service reported Running.
/// </summary>
public sealed class KioskFixR10jGuardHiveTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";

    [Fact]
    public void writable_policy_path_is_null_when_kiosk_hive_not_loaded()
    {
        string? asked = null;
        var path = KioskRegistryPolicyTemplate.ResolveWritableKioskPolicyPath(
            "  " + Sid + " ", KioskRegistryPolicyTemplate.SystemPoliciesRelativePath, s => { asked = s; return false; });
        Assert.Null(path);
        Assert.Equal(Sid, asked);
    }

    [Fact]
    public void writable_policy_path_targets_loaded_hive()
    {
        var path = KioskRegistryPolicyTemplate.ResolveWritableKioskPolicyPath(
            Sid, KioskRegistryPolicyTemplate.DisallowRunRelativePath, _ => true);
        Assert.Equal(Sid + "\\" + KioskRegistryPolicyTemplate.DisallowRunRelativePath, path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void writable_policy_path_is_null_without_sid_and_never_probes_hive(string? sid)
    {
        var probed = false;
        var path = KioskRegistryPolicyTemplate.ResolveWritableKioskPolicyPath(
            sid, KioskRegistryPolicyTemplate.SystemPoliciesRelativePath, _ => { probed = true; return true; });
        Assert.Null(path);
        Assert.False(probed);
    }

    [Fact]
    public void guard_registry_store_gates_writes_on_loaded_hive()
    {
        var store = ReadGuardSource("RegistryPolicyStore.cs");
        var open = store.Substring(store.IndexOf("private static RegistryKey? OpenKioskHive", StringComparison.Ordinal));
        Assert.Contains("KioskRegistryPolicyTemplate.ResolveWritableKioskPolicyPath(sid, relativePath, HiveLoaded)", open);
        Assert.Contains("if (fullPath is null)", open);
        Assert.Contains("Registry.Users.OpenSubKey(sid, writable: false)", store);
    }

    [Fact]
    public void guard_watchdog_policy_writes_never_escape_the_thread()
    {
        var sup = ReadGuardSource("KioskShellSupervisor.cs");
        // Every policy write goes through TryApplyPolicies / TryRepairPolicies, which check the hive and catch.
        Assert.Equal(1, Count(sup, "RegistryPolicyStore.EnsureDefaults();"));
        Assert.Equal(1, Count(sup, "RegistryPolicyStore.Tampered()"));
        Assert.Contains("TryApplyPolicies(\"startup\");", sup);
        Assert.Contains("TryRepairPolicies();", sup);
        Assert.Contains("TryApplyPolicies(\"before_launch\");", sup);
        foreach (var method in new[] { "private void TryApplyPolicies(", "private void TryRepairPolicies(" })
        {
            var body = sup.Substring(sup.IndexOf(method, StringComparison.Ordinal));
            body = body.Substring(0, body.IndexOf("\n    }\n", StringComparison.Ordinal));
            Assert.Contains("if (!RegistryPolicyStore.KioskHiveLoaded())", body);
            Assert.Contains("catch (Exception ex)", body);
            Assert.Contains("guard.policy_apply_failed", body);
        }

        var program = ReadGuardSource("Program.cs");
        Assert.Contains("guard.watchdog_failed", program);
        Assert.Contains("guard.service_identity_unresolved", program);
    }

    private static int Count(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }

    private static string ReadGuardSource(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GameClub.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            Assert.Fail("repo root (GameClub.slnx) not found above " + AppContext.BaseDirectory);
        }

        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "GameClub.Kiosk.Guard", file)).Replace("\r\n", "\n");
    }
}
