#if NET48
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

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

/// <summary>Brief §10.4 — real localhost-store firewall on windows-2022.</summary>
[Collection(WindowsGuardIntegrationCollection.Name)]
[Trait("Category", "PcInternetFirewall")]
public sealed class WindowsPcInternetFirewallIntegrationTests
{
    private static void SkipUnlessWindowsAdmin()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new SkipTestException("Windows-only firewall integration.");
        }

        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new SkipTestException("Administrator elevation required for firewall integration.");
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

    [Fact]
    public void internet_off_blocks_second_resolved_host_and_reports_local_profiles()
    {
        SkipUnlessWindowsAdmin();
        var ips = ResolveServerIps();
        if (ips.Length == 0)
        {
            throw new SkipTestException("Could not resolve api.github.com for server allow-list.");
        }

        string[]? secondHostIps = null;
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
            Assert.Equal("applied", applier.Apply(config, on: false));
            Thread.Sleep(1_500);
            var script = "Get-NetFirewallProfile -PolicyStore ActiveStore | Select-Object Name,DefaultOutboundAction,AllowLocalFirewallRules | ConvertTo-Json";
            var json = RunPowerShell(script);
            Assert.Contains("Block", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("False", json, StringComparison.Ordinal);
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
        SkipUnlessWindowsAdmin();
        if (!Socket.OSSupportsIPv6)
        {
            throw new SkipTestException("IPv6 not supported on runner.");
        }

        try
        {
            using var tcp = new TcpClient(AddressFamily.InterNetworkV6);
            var connect = tcp.ConnectAsync("2606:4700:4700::1111", 443);
            if (!connect.Wait(2_000) || !tcp.Connected)
            {
                throw new SkipTestException("Runner has no working IPv6 egress path to probe.");
            }
        }
        catch (SkipTestException)
        {
            throw;
        }
        catch
        {
            throw new SkipTestException("Runner has no working IPv6 egress path to probe.");
        }

        var ips = ResolveServerIps();
        if (ips.Length == 0)
        {
            throw new SkipTestException("Could not resolve api.github.com for server allow-list.");
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
            Assert.Equal("applied", applier.Apply(config, on: false));
            Assert.False(TryTcpV6("2606:4700:4700::1111", 443, 3_000), "IPv6 egress must be blocked while OFF.");
        }
        finally
        {
            applier.RunResetScript();
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

    [Fact]
    public void internet_off_allows_github_blocks_public_then_reset_restores()
    {
        SkipUnlessWindowsAdmin();
        var ips = ResolveServerIps();
        if (ips.Length == 0)
        {
            throw new SkipTestException("Could not resolve api.github.com for server allow-list.");
        }

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
            Assert.Equal("applied", applier.Apply(config, on: false));
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
        SkipUnlessWindowsAdmin();
        var guardExe = Path.Combine(AppContext.BaseDirectory, "GameClub.Kiosk.Guard.exe");
        if (!File.Exists(guardExe))
        {
            throw new SkipTestException("GameClub.Kiosk.Guard.exe not found beside test output.");
        }

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
        SkipUnlessWindowsAdmin();
        var ips = ResolveServerIps();
        if (ips.Length == 0)
        {
            throw new SkipTestException("Could not resolve api.github.com for server allow-list.");
        }

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
            applier.Apply(config, on: false);
            System.Threading.Thread.Sleep(1_500);
            _ = RunPowerShell(addBypass);
            Assert.False(TryTcp("1.1.1.1", 443, 2_000), "Default-store allow rule must not bypass localhost OFF.");
        }
        finally
        {
            RunPowerShell($"Remove-NetFirewallRule -DisplayName '{bypassRuleName}' -ErrorAction SilentlyContinue");
            applier.RunResetScript();
        }
    }

    [Fact]
    public void internet_on_restores_public_egress_after_off_cycle()
    {
        SkipUnlessWindowsAdmin();
        var ips = ResolveServerIps();
        if (ips.Length == 0)
        {
            throw new SkipTestException("Could not resolve api.github.com for server allow-list.");
        }

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
            Assert.Equal("applied", applier.Apply(config, on: false));
            Assert.False(TryTcp("1.1.1.1", 443, 2_000));
            Assert.Equal("applied", applier.Apply(config, on: true));
            Assert.True(TryTcp("1.1.1.1", 443, 5_000));
        }
        finally
        {
            applier.RunResetScript();
        }
    }

    private static string RunPowerShell(string script)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("powershell");
        proc.WaitForExit(60_000);
        return proc.StandardOutput.ReadToEnd();
    }
}
#endif