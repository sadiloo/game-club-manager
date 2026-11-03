using GameClub.Billing;

namespace GameClub.Kiosk.Core.Tests;

public class SupportTests
{
    [Theory]
    [InlineData(6, 1, 7601, true, false)]
    [InlineData(6, 2, 9200, false, true)]
    [InlineData(6, 3, 9600, true, true)]
    [InlineData(10, 0, 19045, true, true)]
    [InlineData(10, 0, 22631, true, true)]
    public void Windows_version_gates(int major, int minor, int build, bool supported, bool webSockets)
    {
        var version = new Version(major, minor, build);
        var support = WindowsSupport.Classify(version);
        if (major == 6 && minor == 2)
        {
            Assert.Equal(KioskOsSupport.UpgradeWindows8To81, support);
        }
        else
        {
            Assert.Equal(supported ? KioskOsSupport.Supported : KioskOsSupport.TooOld, support);
        }

        Assert.Equal(webSockets, WindowsSupport.ClientWebSocketSupported(version));
    }

    [Fact]
    public void SignalR_on_windows_7_uses_sse_and_long_polling_without_websockets()
    {
        var win7 = SignalRTransports.Select(WindowsSupport.ClientWebSocketSupported(new Version(6, 1, 7601)));
        Assert.False(SignalRTransports.UsesWebSockets(win7));
        Assert.True(SignalRTransports.UsesServerSentEvents(win7));
        Assert.True(SignalRTransports.UsesLongPolling(win7));

        var win11 = SignalRTransports.Select(true);
        Assert.True(SignalRTransports.UsesWebSockets(win11));
        Assert.True(SignalRTransports.UsesServerSentEvents(win11));
        Assert.True(SignalRTransports.UsesLongPolling(win11));
        Assert.Equal(1, SignalRTransports.WebSockets);
        Assert.Equal(2, SignalRTransports.ServerSentEvents);
        Assert.Equal(4, SignalRTransports.LongPolling);
    }

    [Fact]
    public void Tehran_now_uses_the_fixed_offset_even_if_the_pc_clock_is_utc()
    {
        var utc = new DateTimeOffset(2026, 6, 2, 6, 30, 0, TimeSpan.Zero);
        var tehran = WindowsSupport.TehranNow(utc);
        Assert.Equal(TimeSpan.FromHours(3.5), tehran.Offset);
        Assert.Equal(10, tehran.Hour);
        Assert.Equal(0, tehran.Minute);
    }

    [Fact]
    public void Admin_gate_locks_after_repeated_failures_and_roundtrips_the_hash()
    {
        var hash = Pbkdf2Hash.Create("cafe-owner");
        var restored = Pbkdf2Hash.FromJson(hash.ToJson());
        var gate = new AdminGate(restored);
        var now = CafeKit.At(9, 0);
        Assert.False(gate.TryOpen("wrong", now, TimeSpan.FromMinutes(10)));
        Assert.False(gate.TryOpen("", now, TimeSpan.FromMinutes(10)));
        Assert.True(gate.TryOpen("cafe-owner", now, TimeSpan.FromMinutes(10)));
        Assert.True(gate.IsActive(now.AddMinutes(9)));
        Assert.False(gate.IsActive(now.AddMinutes(10)));

        var locked = new AdminGate(hash);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.False(locked.TryOpen("no", now, TimeSpan.FromMinutes(10)));
        }

        Assert.False(locked.TryOpen("cafe-owner", now.AddSeconds(30), TimeSpan.FromMinutes(10)));
        Assert.True(locked.TryOpen("cafe-owner", now.AddMinutes(1), TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Lockdown_is_per_user_registry_and_has_no_shell_launcher()
    {
        Assert.Contains(LockdownCatalog.PerUser, item => item.Name == "DisableTaskMgr");
        Assert.Contains(LockdownCatalog.PerUser, item => item.Name == "NoWinKeys");
        Assert.Contains(LockdownCatalog.PerUser, item => item.Name == "NoRun");
        Assert.Contains(LockdownCatalog.PerUser, item => item.Name == "NoControlPanel");
        Assert.Contains(LockdownCatalog.PerUser, item => item.Name == "DisableLockWorkstation");
        Assert.Contains(LockdownCatalog.Machine, item => item.Name == "NoDriveTypeAutoRun" && item.Data == "255");
        Assert.Contains(LockdownCatalog.Machine, item => item.Name == "SchUseStrongCrypto");
        Assert.Contains(LockdownCatalog.Machine, item => item.Name == "DefaultSecureProtocols" && item.Data == "2560");
        Assert.Contains(LockdownCatalog.Machine, item => item.Key.Contains("TLS 1.2"));
        Assert.Equal("Shell", LockdownCatalog.PerUserShellValue);
        Assert.DoesNotContain(LockdownCatalog.Machine, item => item.Key == LockdownCatalog.MachineWinlogonKey && item.Name == "Shell");
        var described = string.Join(" ", LockdownCatalog.PerUser.Select(item => item.Name + item.Key));
        Assert.DoesNotContain("ShellLauncher", described);
        Assert.DoesNotContain("AssignedAccess", described);
    }

    [Fact]
    public void Install_scripts_set_the_per_user_shell_and_do_not_replace_the_machine_shell()
    {
        foreach (var name in new[] { "install-kiosk.cmd", "uninstall-kiosk.cmd", "install-kiosk.ps1", "uninstall-kiosk.ps1" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, "scripts", name);
            var text = File.ReadAllText(path);
            Assert.Contains("DisableTaskMgr", text);
            Assert.Contains("NoWinKeys", text);
            Assert.Contains("NoRun", text);
            Assert.Contains("NoControlPanel", text);
            Assert.Contains("NoDriveTypeAutoRun", text);
            Assert.Contains("Winlogon", text);
            Assert.DoesNotContain("ShellLauncher", text);
            Assert.DoesNotContain("AssignedAccess", text);
        }

        var install = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scripts", "install-kiosk.cmd"));
        Assert.Contains("SchUseStrongCrypto", install);
        Assert.Contains("TLS 1.2", install);
        Assert.Contains("Windows 8.1", install);
        Assert.Contains("HKU", install);
        Assert.DoesNotContain(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", install);

        var ps1 = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scripts", "install-kiosk.ps1"));
        Assert.Contains("#requires -Version 2", ps1);
        Assert.Contains("SchUseStrongCrypto", ps1);
        Assert.DoesNotContain("$PSScriptRoot", ps1);
        Assert.DoesNotContain("ConvertTo-Json", ps1);
        Assert.DoesNotContain("Get-CimInstance", ps1);
    }

    [Fact]
    public void Account_snapshot_roundtrips_packages()
    {
        var account = CafeKit.WithPackages(
            4_000,
            new GameClub.Billing.TimePackage(
                "night",
                15,
                20_000,
                new HashSet<string> { "pc-normal" },
                new TimeOfDay(0, 0),
                new TimeOfDay(8, 0),
                new LocalDate(2026, 12, 31)));
        var restored = AccountCodec.ToAccount(AccountCodec.Read(AccountCodec.Write(AccountCodec.From(account))));
        Assert.Equal(4_000, restored.PcMoneyToman);
        Assert.Equal(15, restored.Packages[0].RemainingMinutes);
        Assert.Equal(new LocalDate(2026, 12, 31), restored.Packages[0].ValidThrough);
        Assert.Contains("pc-normal", restored.Packages[0].AllowedGroupIds!);
    }
}
