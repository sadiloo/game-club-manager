#if NET48
#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using GameClub.Kiosk.Guard.Internet;
using Xunit;
using Xunit.Abstractions;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

/// <summary>Brief §10.4 — real localhost-store firewall on windows-2022.</summary>
[Collection(WindowsGuardIntegrationCollection.Name)]
[Trait("Category", "PcInternetFirewall")]
public sealed class WindowsPcInternetFirewallIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public WindowsPcInternetFirewallIntegrationTests(ITestOutputHelper output) => _output = output;

    private void RequireWindowsAdmin()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Fail("Windows-only firewall integration.");
        }

        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Assert.Fail("Administrator elevation required for firewall integration.");
        }
    }

    private static string[] ResolveServerIps()
    {
        try
        {
            return Dns.GetHostAddresses("api.github.com")
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .Distinct()
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string[] CiExtraPrograms()
    {
        var fromEnv = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_EXTRA_ALLOW");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        var listener = Environment.GetEnvironmentVariable("RUNNER_ROOT") is { } root
            ? Path.Combine(root, "bin", "Runner.Listener.exe")
            : null;
        if (listener is not null && File.Exists(listener))
        {
            return [listener];
        }

        return [];
    }

    private static bool TryTcp(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(timeoutMs) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryTcpV6(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetworkV6);
            var task = client.ConnectAsync(host, port);
            return task.Wait(timeoutMs) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static bool RunnerIpv6EgressProbe()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return false;
        }

        try
        {
            using var tcp = new TcpClient(AddressFamily.InterNetworkV6);
            var connect = tcp.ConnectAsync("2606:4700:4700::1111", 443);
            return connect.Wait(2_000) && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static void AssertActiveStoreOffProfiles(string json)
    {
        Assert.Contains("Block", json, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string FirewallProfileSelectJsonScript =
        "Select-Object Name,@{N='DefaultOutboundAction';E={$_.DefaultOutboundAction.ToString()}},@{N='AllowLocalFirewallRules';E={$_.AllowLocalFirewallRules.ToString()}} | ConvertTo-Json";

    private static readonly string FirewallProfileEchoScript = """
        Get-NetFirewallProfile -PolicyStore $store | ForEach-Object {
          Write-Output "FW_STORE_ECHO store=$store profile=$($_.Name) DefaultOutboundAction=$($_.DefaultOutboundAction.ToString()) AllowLocalFirewallRules=$($_.AllowLocalFirewallRules.ToString())"
        }
        """;

    private static readonly string FirewallProfileEchoRsopScript = """
        Get-NetFirewallProfile -PolicyStore $store -ErrorAction SilentlyContinue | ForEach-Object {
          Write-Output "FW_STORE_ECHO_RSOP store=$store profile=$($_.Name) DefaultOutboundAction=$($_.DefaultOutboundAction.ToString()) AllowLocalFirewallRules=$($_.AllowLocalFirewallRules.ToString())"
        }
        """;

    private void EchoFirewallStoreProfiles(string policyStore)
    {
        var script = $"$store = '{policyStore}'; {FirewallProfileEchoScript}";
        var echo = WindowsIntegrationRunPowerShell.Run(script);
        Console.WriteLine(echo);
        _output.WriteLine(echo);
    }

    private void EchoRsopFirewallStoreProfiles()
    {
        foreach (var store in new[] { "RSOP", "GroupPolicy" })
        {
            var script = $"$store = '{store}'; {FirewallProfileEchoRsopScript}";
            try
            {
                var echo = WindowsIntegrationRunPowerShell.Run(script, tolerateNonZeroExit: true);
                Console.WriteLine(echo);
                _output.WriteLine(echo);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FW_STORE_ECHO_RSOP store={store} read_failed={ex.Message}");
            }
        }
    }

    private static void AssertPersistentStoreOffProfiles(string persistentJson)
    {
        Assert.Contains("Block", persistentJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("False", persistentJson, StringComparison.Ordinal);
    }

    private void AssertOffFirewallStoresAfterApply()
    {
        EchoFirewallStoreProfiles("ActiveStore");
        EchoFirewallStoreProfiles("PersistentStore");
        EchoRsopFirewallStoreProfiles();
        var activeJson = WindowsIntegrationRunPowerShell.Run(
            $"Get-NetFirewallProfile -PolicyStore ActiveStore | {FirewallProfileSelectJsonScript}");
        AssertActiveStoreOffProfiles(activeJson);
        var persistentJson = WindowsIntegrationRunPowerShell.Run(
            $"Get-NetFirewallProfile -PolicyStore PersistentStore | {FirewallProfileSelectJsonScript}");
        AssertPersistentStoreOffProfiles(persistentJson);
    }

    private static void AssertLocalhostProfilesNotBlock()
    {
        var profileJson = WindowsIntegrationRunPowerShell.Run(
            "Get-NetFirewallProfile -PolicyStore localhost | Select-Object Name,@{N='DefaultOutboundAction';E={$_.DefaultOutboundAction.ToString()}} | ConvertTo-Json");
        Assert.DoesNotContain("Block", profileJson, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertCheckpointOffRuleGroupPresent()
    {
        var script =
            """
            $rules = Get-NetFirewallRule -Group 'Checkpoint Internet Off' -ErrorAction SilentlyContinue
            if (-not $rules) { throw 'missing Checkpoint Internet Off rules' }
            ($rules | Measure-Object).Count
            """;
        var countText = WindowsIntegrationRunPowerShell.Run(script).Trim();
        Assert.True(int.TryParse(countText, out var count) && count > 0, "Checkpoint Internet Off rule group must be installed.");
    }

    [Fact]
    public void internet_off_blocks_second_resolved_host_and_reports_local_profiles()
    {
        RequireWindowsAdmin();
        var ips = ResolveServerIps();
        Assert.True(ips.Length > 0, "Could not resolve api.github.com for server allow-list.");

        string[] secondHostIps = null;
        try
        {
            secondHostIps = Dns.GetHostAddresses("example.com")
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .Distinct()
                .Take(1)
                .ToArray();
        }
        catch
        {
            /* optional */
        }

        var applier = new InternetFirewallApplier();
        var config = new InternetGuardConfig
        {
            StationId = "CI-PC-0",
            ServerHost = "api.github.com",
            ServerPort = 443,
            ServerIps = ips,
            ExtraAllowPrograms = CiExtraPrograms(),
        };
        try
        {
            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: false));
            Thread.Sleep(1_500);
            AssertOffFirewallStoresAfterApply();
            Assert.True(TryTcp("127.0.0.1", 80, 1_000), "loopback must remain reachable while OFF.");
            if (secondHostIps is { Length: > 0 })
            {
                Assert.False(TryTcp(secondHostIps[0], 443, 2_000), "Second public host must be blocked while OFF.");
            }
        }
        finally
        {
            applier.RunResetScript();
        }
    }

    [Fact]
    public void internet_off_ipv6_egress_blocked_when_available()
    {
        RequireWindowsAdmin();
        var ips = ResolveServerIps();
        Assert.True(ips.Length > 0, "Could not resolve api.github.com for server allow-list.");

        var hadIpv6Egress = RunnerIpv6EgressProbe();
        if (!hadIpv6Egress)
        {
            _output.WriteLine("ipv6_probe_absent: runner has no working IPv6 egress; asserting localhost OFF store and rule group only.");
        }

        var applier = new InternetFirewallApplier();
        var config = new InternetGuardConfig
        {
            StationId = "CI-PC-IPV6",
            ServerHost = "api.github.com",
            ServerPort = 443,
            ServerIps = ips,
            ExtraAllowPrograms = CiExtraPrograms(),
        };
        try
        {
            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: false));
            AssertOffFirewallStoresAfterApply();
            AssertCheckpointOffRuleGroupPresent();
            if (hadIpv6Egress)
            {
                Assert.False(TryTcpV6("2606:4700:4700::1111", 443, 3_000), "IPv6 egress must be blocked while OFF.");
            }
        }
        finally
        {
            applier.RunResetScript();
        }
    }

    [Fact]
    public void internet_off_allows_github_blocks_public_then_reset_restores()
    {
        RequireWindowsAdmin();
        var ips = ResolveServerIps();
        Assert.True(ips.Length > 0, "Could not resolve api.github.com for server allow-list.");

        var applier = new InternetFirewallApplier();
        var config = new InternetGuardConfig
        {
            StationId = "CI-PC-1",
            ServerHost = "api.github.com",
            ServerPort = 443,
            ServerIps = ips,
            ExtraAllowPrograms = CiExtraPrograms(),
        };

        try
        {
            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: false));
            Thread.Sleep(2_000);
            Assert.True(TryTcp("api.github.com", 443, 5_000), "OFF must still allow configured server host.");
            Assert.False(TryTcp("1.1.1.1", 443, 2_000), "OFF must block arbitrary public egress.");
        }
        finally
        {
            applier.RunResetScript();
            Thread.Sleep(1_000);
            Assert.True(TryTcp("1.1.1.1", 443, 5_000), "internet-reset must restore outbound access.");
        }
    }

    [Fact]
    public void internet_configure_cli_writes_config_with_extra_allow()
    {
        RequireWindowsAdmin();
        var guardExe = Path.Combine(AppContext.BaseDirectory, "GameClub.Kiosk.Guard.exe");
        Assert.True(File.Exists(guardExe), "GameClub.Kiosk.Guard.exe not found beside test output.");

        var kioskDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Checkpoint", "kiosk");
        Directory.CreateDirectory(kioskDir);
        var devicePath = Path.Combine(kioskDir, "device.json");
        File.WriteAllText(devicePath,
            """{"StationId":"CI-PC-2","ServerBase":"https://api.github.com","LeaseVerifyPublicKeyPem":""}""");

        var extra = string.Join(";", CiExtraPrograms());
        var args = string.IsNullOrEmpty(extra)
            ? "internet-configure"
            : $"internet-configure --extra {extra}";
        var psi = new ProcessStartInfo(guardExe, args)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Guard cli failed to start.");
        proc.WaitForExit(30_000);
        Assert.Equal(0, proc.ExitCode);
        Assert.True(File.Exists(InternetGuardPaths.ConfigPath));
    }

    [Fact]
    public void internet_off_third_party_allow_in_default_store_does_not_bypass_localhost_off()
    {
        RequireWindowsAdmin();
        var ips = ResolveServerIps();
        Assert.True(ips.Length > 0, "Could not resolve api.github.com for server allow-list.");

        var applier = new InternetFirewallApplier();
        var config = new InternetGuardConfig
        {
            StationId = "CI-PC-3",
            ServerHost = "api.github.com",
            ServerPort = 443,
            ServerIps = ips,
            ExtraAllowPrograms = CiExtraPrograms(),
        };
        var bypassRuleName = "GameClub Test Allow";
        try
        {
            var addBypass = $"New-NetFirewallRule -DisplayName '{bypassRuleName}' -Direction Outbound -Action Allow -Program (Get-Command pwsh).Source";
            applier.ApplyWithStoreEcho(config, on: false);
            System.Threading.Thread.Sleep(1_500);
            _ = WindowsIntegrationRunPowerShell.Run(addBypass);
            Assert.False(TryTcp("1.1.1.1", 443, 2_000), "Default-store allow rule must not bypass localhost OFF.");
        }
        finally
        {
            WindowsIntegrationRunPowerShell.Run($"Remove-NetFirewallRule -DisplayName '{bypassRuleName}' -ErrorAction SilentlyContinue");
            applier.RunResetScript();
        }
    }

    [Fact]
    public void internet_on_restores_public_egress_after_off_cycle()
    {
        RequireWindowsAdmin();
        var ips = ResolveServerIps();
        Assert.True(ips.Length > 0, "Could not resolve api.github.com for server allow-list.");

        var applier = new InternetFirewallApplier();
        var config = new InternetGuardConfig
        {
            StationId = "CI-PC-4",
            ServerHost = "api.github.com",
            ServerPort = 443,
            ServerIps = ips,
            ExtraAllowPrograms = CiExtraPrograms(),
        };
        try
        {
            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: false));
            Assert.False(TryTcp("1.1.1.1", 443, 2_000));
            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: true));
            Assert.True(TryTcp("1.1.1.1", 443, 5_000));
        }
        finally
        {
            applier.RunResetScript();
        }
    }

    [Fact]
    public void internet_drift_production_default_interval_is_60_seconds()
    {
        Assert.Equal(60_000, InternetDriftWatchdog.ProductionDriftIntervalMs);
    }

    [Fact]
    public void internet_off_first_apply_succeeds_when_checkpoint_rule_group_is_empty()
    {
        RequireWindowsAdmin();
        var ips = ResolveServerIps();
        Assert.True(ips.Length > 0, "Could not resolve api.github.com for server allow-list.");

        var applier = new InternetFirewallApplier();
        var config = new InternetGuardConfig
        {
            StationId = "CI-EMPTY-GROUP",
            ServerHost = "api.github.com",
            ServerPort = 443,
            ServerIps = ips,
            ExtraAllowPrograms = CiExtraPrograms(),
        };
        try
        {
            applier.RunResetScript();
            Thread.Sleep(500);
            var preCount = WindowsIntegrationRunPowerShell.Run(
                "(Get-NetFirewallRule -PolicyStore localhost -Group 'Checkpoint Internet Off' -ErrorAction SilentlyContinue | Measure-Object).Count").Trim();
            Assert.True(int.TryParse(preCount, out var count) && count == 0, "precondition: Checkpoint Internet Off group must be empty.");

            var result = applier.ApplyWithStoreEcho(config, on: false);
            Assert.Equal("applied", result);
            AssertCheckpointOffRuleGroupPresent();
            Assert.True(TryTcp("api.github.com", 443, 5_000), "first apply must leave server host reachable.");
        }
        finally
        {
            applier.RunResetScript();
        }
    }

    [Fact]
    public void internet_off_reapply_after_rules_removed_while_blocked_restores_server_allow()
    {
        RequireWindowsAdmin();
        var ips = ResolveServerIps();
        Assert.True(ips.Length > 0, "Could not resolve api.github.com for server allow-list.");

        var applier = new InternetFirewallApplier();
        var config = new InternetGuardConfig
        {
            StationId = "CI-REAPPLY",
            ServerHost = "api.github.com",
            ServerPort = 443,
            ServerIps = ips,
            ExtraAllowPrograms = CiExtraPrograms(),
        };
        try
        {
            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: false));
            WindowsIntegrationRunPowerShell.Run(
                "Get-NetFirewallRule -Group 'Checkpoint Internet Off' | Remove-NetFirewallRule");
            var groupCountText = WindowsIntegrationRunPowerShell.Run(
                "(Get-NetFirewallRule -Group 'Checkpoint Internet Off' -ErrorAction SilentlyContinue | Measure-Object).Count").Trim();
            Assert.True(int.TryParse(groupCountText, out var groupCount) && groupCount == 0,
                "precondition: Checkpoint Internet Off rule group must be empty after removal.");
            Assert.False(TryTcp("api.github.com", 443, 3_000), "precondition: blocked profiles without allow rules must drop server egress.");

            var result = applier.ApplyWithStoreEcho(config, on: false);
            Assert.Equal("applied", result);
            Assert.True(TryTcp("api.github.com", 443, 5_000), "re-apply must recreate allow rules before verification.");
        }
        finally
        {
            applier.RunResetScript();
        }
    }

    [Fact]
    public void internet_off_apply_failure_after_profiles_set_rolls_back_localhost_profiles()
    {
        RequireWindowsAdmin();
        var ips = ResolveServerIps();
        Assert.True(ips.Length > 0, "Could not resolve api.github.com for server allow-list.");

        var applier = new InternetFirewallApplier();
        var config = new InternetGuardConfig
        {
            StationId = "CI-ROLLBACK",
            ServerHost = "api.github.com",
            ServerPort = 443,
            ServerIps = ips,
            ExtraAllowPrograms = CiExtraPrograms(),
        };
        var priorSeam = InternetFirewallApplierTestSeams.ThrowAfterProfilesBeforeVerify;
        try
        {
            applier.RunResetScript();
            Thread.Sleep(500);
            InternetFirewallApplierTestSeams.ThrowAfterProfilesBeforeVerify = true;
            var result = applier.ApplyWithStoreEcho(config, on: false);
            Assert.StartsWith("apply_failed", result, StringComparison.Ordinal);
            AssertLocalhostProfilesNotBlock();
            Assert.True(TryTcp(config.ServerHost, 443, 5_000), "server host must stay reachable when OFF apply fails after profiles.");
        }
        finally
        {
            InternetFirewallApplierTestSeams.ThrowAfterProfilesBeforeVerify = priorSeam;
            applier.RunResetScript();
        }
    }

    [Fact]
    public void internet_off_missing_server_config_returns_firewall_not_configured()
    {
        var applier = new InternetFirewallApplier();
        var config = new InternetGuardConfig
        {
            StationId = "CI-NOCONF",
            ServerHost = "api.github.com",
            ServerPort = 0,
            ServerIps = Array.Empty<string>(),
        };
        Assert.Equal("firewall_not_configured", applier.ApplyWithStoreEcho(config, on: false));
    }

    [Fact]
    public void internet_off_apply_result_is_applied_only_after_verified_active_store_block()
    {
        RequireWindowsAdmin();
        var ips = ResolveServerIps();
        Assert.True(ips.Length > 0, "Could not resolve api.github.com for server allow-list.");

        var applier = new InternetFirewallApplier();
        var config = new InternetGuardConfig
        {
            StationId = "CI-VERIFY",
            ServerHost = "api.github.com",
            ServerPort = 443,
            ServerIps = ips,
            ExtraAllowPrograms = CiExtraPrograms(),
        };
        try
        {
            var result = applier.ApplyWithStoreEcho(config, on: false);
            Assert.Equal("applied", result);
            Assert.False(TryTcp("1.1.1.1", 443, 2_000));
            AssertOffFirewallStoresAfterApply();
            AssertCheckpointOffRuleGroupPresent();
        }
        finally
        {
            applier.RunResetScript();
        }
    }

    [Fact]
    public void internet_off_then_on_restores_active_store_without_block_or_group_rules()
    {
        RequireWindowsAdmin();
        var ips = ResolveServerIps();
        Assert.True(ips.Length > 0, "Could not resolve api.github.com for server allow-list.");

        var applier = new InternetFirewallApplier();
        var config = new InternetGuardConfig
        {
            StationId = "CI-OFF-ON",
            ServerHost = "api.github.com",
            ServerPort = 443,
            ServerIps = ips,
            ExtraAllowPrograms = CiExtraPrograms(),
        };
        try
        {
            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: false));
            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: true));
            var activeRuleCount = int.Parse(WindowsIntegrationRunPowerShell.Run(
                "(Get-NetFirewallRule -Group 'Checkpoint Internet Off' -ErrorAction SilentlyContinue | Measure-Object).Count").Trim());
            Assert.Equal(0, activeRuleCount);
            var profileJson = WindowsIntegrationRunPowerShell.Run(
                "Get-NetFirewallProfile | Select-Object Name,@{N='DefaultOutboundAction';E={$_.DefaultOutboundAction.ToString()}} | ConvertTo-Json");
            Assert.DoesNotContain("Block", profileJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            applier.ApplyWithStoreEcho(config, on: true);
        }
    }

    [Fact]
    public void internet_off_apply_cleans_legacy_localhost_store_before_persistent_off()
    {
        RequireWindowsAdmin();
        var ips = ResolveServerIps();
        Assert.True(ips.Length > 0, "Could not resolve api.github.com for server allow-list.");

        var applier = new InternetFirewallApplier();
        var config = new InternetGuardConfig
        {
            StationId = "CI-LOCALHOST-CLEAN",
            ServerHost = "api.github.com",
            ServerPort = 443,
            ServerIps = ips,
            ExtraAllowPrograms = CiExtraPrograms(),
        };
        const string seedScript = """
            $group = 'Checkpoint Internet Off'
            New-NetFirewallRule -PolicyStore localhost -DisplayName 'Legacy Local' -Group $group -Direction Outbound -Action Allow -Protocol TCP -RemotePort 443 | Out-Null
            Set-NetFirewallProfile -PolicyStore localhost -Name Public -DefaultOutboundAction Block -AllowLocalFirewallRules False
            """;
        try
        {
            WindowsIntegrationRunPowerShell.Run(seedScript);
            var preLocal = int.Parse(WindowsIntegrationRunPowerShell.Run(
                "(Get-NetFirewallRule -PolicyStore localhost -Group 'Checkpoint Internet Off' -ErrorAction SilentlyContinue | Measure-Object).Count").Trim());
            Assert.True(preLocal >= 1, "precondition: localhost legacy rules seeded");

            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: false));
            var postLocal = int.Parse(WindowsIntegrationRunPowerShell.Run(
                "(Get-NetFirewallRule -PolicyStore localhost -Group 'Checkpoint Internet Off' -ErrorAction SilentlyContinue | Measure-Object).Count").Trim());
            Assert.Equal(0, postLocal);
            var activeCount = int.Parse(WindowsIntegrationRunPowerShell.Run(
                "(Get-NetFirewallRule -Group 'Checkpoint Internet Off' -ErrorAction SilentlyContinue | Measure-Object).Count").Trim());
            Assert.True(activeCount >= 1);
        }
        finally
        {
            applier.ApplyWithStoreEcho(config, on: true);
        }
    }

    [Fact]
    public void internet_guard_startup_clears_orphan_partial_block_when_state_file_missing()
    {
        RequireWindowsAdmin();
        var ips = ResolveServerIps();
        Assert.True(ips.Length > 0, "Could not resolve api.github.com for server allow-list.");

        var applier = new InternetFirewallApplier();
        var config = new InternetGuardConfig
        {
            StationId = "CI-STARTUP-PARTIAL",
            ServerHost = "api.github.com",
            ServerPort = 443,
            ServerIps = ips,
            ExtraAllowPrograms = CiExtraPrograms(),
        };
        var watchdogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Checkpoint",
            "guard",
            "watchdog.log");
        try
        {
            TryDeleteState();
            WindowsIntegrationRunPowerShell.Run(
                "Set-NetFirewallProfile -Name Public -DefaultOutboundAction Block -AllowLocalFirewallRules False");
            Assert.True(TryTcp("1.1.1.1", 443, 2_000) is false, "precondition: partial block should restrict egress");

            InternetGuardStartupFirewall.ReconcileOnPipeStart(config, applier);

            Assert.True(TryTcp("1.1.1.1", 443, 3_000), "startup must unblock partial Block without verified state");
            var profileJson = WindowsIntegrationRunPowerShell.Run(
                "Get-NetFirewallProfile | Select-Object Name,@{N='DefaultOutboundAction';E={$_.DefaultOutboundAction.ToString()}} | ConvertTo-Json");
            Assert.DoesNotContain("Block", profileJson, StringComparison.OrdinalIgnoreCase);
            var activeRules = int.Parse(WindowsIntegrationRunPowerShell.Run(
                "(Get-NetFirewallRule -Group 'Checkpoint Internet Off' -ErrorAction SilentlyContinue | Measure-Object).Count").Trim());
            Assert.Equal(0, activeRules);
            if (File.Exists(watchdogPath))
            {
                var tail = GuardWatchdogTimelineReader.ReadAllTextWithShare();
                Assert.Contains("reconcile_reset=applied", tail, StringComparison.Ordinal);
            }
        }
        finally
        {
            applier.ApplyWithStoreEcho(config, on: true);
            TryDeleteState();
        }
    }

    [Fact]
    public void internet_guard_startup_clears_orphan_off_when_state_file_missing()
    {
        RequireWindowsAdmin();
        var ips = ResolveServerIps();
        Assert.True(ips.Length > 0, "Could not resolve api.github.com for server allow-list.");

        var applier = new InternetFirewallApplier();
        var config = new InternetGuardConfig
        {
            StationId = "CI-STARTUP-ORPHAN",
            ServerHost = "api.github.com",
            ServerPort = 443,
            ServerIps = ips,
            ExtraAllowPrograms = CiExtraPrograms(),
        };
        try
        {
            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: false));
            if (File.Exists(InternetGuardPaths.StatePath))
            {
                File.Delete(InternetGuardPaths.StatePath);
            }

            InternetGuardStartupFirewall.ReconcileOnPipeStart(config, applier);
            Assert.True(TryTcp("1.1.1.1", 443, 3_000), "startup must unblock when OFF artifacts lack verified state");
            var activeRules = int.Parse(WindowsIntegrationRunPowerShell.Run(
                "(Get-NetFirewallRule -Group 'Checkpoint Internet Off' -ErrorAction SilentlyContinue | Measure-Object).Count").Trim());
            Assert.Equal(0, activeRules);
        }
        finally
        {
            applier.ApplyWithStoreEcho(config, on: true);
            TryDeleteState();
        }
    }

    private static void TryDeleteState()
    {
        try
        {
            if (File.Exists(InternetGuardPaths.StatePath))
            {
                File.Delete(InternetGuardPaths.StatePath);
            }
        }
        catch
        {
        }
    }

}
#endif