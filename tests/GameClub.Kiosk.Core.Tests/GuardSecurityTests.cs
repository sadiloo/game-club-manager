namespace GameClub.Kiosk.Core.Tests;

public class GuardSecurityTests
{
    [Fact]
    public void Pipe_allows_only_system_and_the_kiosk_user()
    {
        var rules = PipeAccess.ForKioskUser("S-1-5-21-9");
        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, rule => rule.Sid == PipeAccess.SystemSid && rule.Rights == "full");
        Assert.Contains(rules, rule => rule.Sid == "S-1-5-21-9" && rule.Rights == "readwrite");
        Assert.DoesNotContain(rules, rule => rule.Sid == "S-1-1-0");
        Assert.Throws<InvalidOperationException>(() => PipeAccess.ForKioskUser(null));
        Assert.Throws<InvalidOperationException>(() => PipeAccess.ForKioskUser("  "));
        Assert.Throws<InvalidOperationException>(() => PipeAccess.ForKioskUser(PipeAccess.SystemSid));
    }

    [Fact]
    public void Escape_and_exit_require_the_guard_password_and_the_shell_image()
    {
        var shell = @"C:\Program Files\GameClub\GameClub.Kiosk.exe";
        var now = CafeKit.At(9, 0);
        var window = TimeSpan.FromMinutes(10);
        var accepted = new AdminGate(Pbkdf2Hash.Create("cafe-owner"));
        var message = new PipeMessage { Kind = "admin-escape", Password = "cafe-owner" };
        Assert.True(GuardCommands.TryAuthorizePrivileged(message, shell, shell, accepted, now, window, out var until, out var reason));
        Assert.Equal("ok", reason);
        Assert.Equal(now.Add(window), until);

        var wrongImage = new AdminGate(Pbkdf2Hash.Create("cafe-owner"));
        Assert.False(GuardCommands.TryAuthorizePrivileged(message, @"C:\Temp\evil.exe", shell, wrongImage, now, window, out _, out var image));
        Assert.Equal("unexpected_process", image);

        var wrongPassword = new AdminGate(Pbkdf2Hash.Create("cafe-owner"));
        var bad = new PipeMessage { Kind = "exit", Password = "nope" };
        Assert.False(GuardCommands.TryAuthorizePrivileged(bad, shell, shell, wrongPassword, now, window, out var closed, out var rejected));
        Assert.Null(closed);
        Assert.Equal("rejected", rejected);

        var exit = new AdminGate(Pbkdf2Hash.Create("cafe-owner"));
        var leaving = new PipeMessage { Kind = "exit", Password = "cafe-owner" };
        Assert.True(GuardCommands.TryAuthorizePrivileged(leaving, shell, shell, exit, now, window, out var exitUntil, out var exitReason));
        Assert.Equal("exit", exitReason);
        Assert.Null(exitUntil);

        Assert.False(GuardCommands.TryAuthorizePrivileged(new PipeMessage { Kind = "heartbeat" }, shell, shell, exit, now, window, out _, out var ignored));
        Assert.Equal("not_privileged", ignored);
    }

    [Fact]
    public async Task Zero_credit_and_logout_publish_a_stop_list_the_guard_kills_only_in_the_kiosk_session()
    {
        await using var scope = await Harness.Start(balance: 2_500);
        var start = CafeKit.At(18, 0);
        scope.Server.SetServerClock(start);
        Assert.True(await scope.Controller.LoginAsync("reza", "reza-demo", start));
        Assert.Empty(scope.Store.ReadStopList());
        Assert.True(scope.Controller.PrepareLaunch("local", WindowsGeneration.Windows10).Allowed);

        scope.Controller.Tick(start.AddMinutes(5));
        Assert.Equal(KioskPhase.Locked, scope.Controller.Screen.Phase);
        Assert.Contains("local", scope.Store.ReadStopList());

        var kiosk = KioskContext();
        var stopped = GuardEnforcement.Combine("local", elevated: false, ["local"], kiosk, scope.Store.ReadStopList());
        Assert.True(stopped.Kill);
        Assert.Equal("session_end", stopped.Reason);

        var admin = kiosk with { SessionId = 2, UserSid = "S-1-5-21-1" };
        var spared = GuardEnforcement.Combine("local", elevated: false, ["local"], admin, scope.Store.ReadStopList());
        Assert.False(spared.Kill);
        Assert.Equal("other_user", spared.Reason);

        await using var logout = await Harness.Start();
        var at = CafeKit.At(18, 0);
        logout.Server.SetServerClock(at);
        Assert.True(await logout.Controller.LoginAsync("reza", "reza-demo", at));
        Assert.True(logout.Controller.PrepareLaunch("local", WindowsGeneration.Windows10).Allowed);
        logout.Server.SetServerClock(at.AddMinutes(3));
        await logout.Controller.LogoutAsync(at.AddMinutes(3));
        Assert.Contains("local", logout.Store.ReadStopList());
        Assert.Equal(
            "session_end",
            GuardEnforcement.Combine("LOCAL.exe", false, ["local"], KioskContext(), logout.Store.ReadStopList()).Reason);
    }

    [Fact]
    public void Windows_sqlite_natives_are_copied_for_x86_and_x64()
    {
        var root = RepoRoot();
        string? folder = null;
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            foreach (var framework in new[] { "net10.0", "net48" })
            {
                var candidate = Path.Combine(root, "src", "GameClub.Kiosk.Core", "bin", configuration, framework);
                var x64 = Path.Combine(candidate, "runtimes", "win-x64", "native", "e_sqlite3.dll");
                var x86 = Path.Combine(candidate, "runtimes", "win-x86", "native", "e_sqlite3.dll");
                if (File.Exists(x64) && File.Exists(x86))
                {
                    folder = candidate;
                }
            }
        }

        Assert.False(string.IsNullOrEmpty(folder));
        Assert.True(new FileInfo(Path.Combine(folder!, "runtimes", "win-x64", "native", "e_sqlite3.dll")).Length > 0);
        Assert.True(new FileInfo(Path.Combine(folder!, "runtimes", "win-x86", "native", "e_sqlite3.dll")).Length > 0);
    }

    [Fact]
    public void Certificate_pin_accepts_the_leaf_or_the_chain_and_rejects_anything_else()
    {
        Assert.True(StationTrust.MatchesPresented("AB:CD", "abcd", null));
        Assert.True(StationTrust.MatchesPresented("ab", null, ["11", "AB"]));
        Assert.False(StationTrust.MatchesPresented("", "AB", ["AB"]));
        Assert.False(StationTrust.MatchesPresented("AB", "CD", ["EF"]));
    }

    [Fact]
    public void Admin_lockout_survives_a_snapshot()
    {
        var hash = Pbkdf2Hash.Create("cafe-owner");
        var gate = new AdminGate(hash);
        var now = CafeKit.At(9, 0);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.False(gate.TryOpen("no", now, TimeSpan.FromMinutes(10)));
        }

        var restored = new AdminGate(hash);
        restored.RestoreJson(gate.SnapshotJson());
        Assert.False(restored.TryOpen("cafe-owner", now.AddSeconds(30), TimeSpan.FromMinutes(10)));
        Assert.True(restored.TryOpen("cafe-owner", now.AddMinutes(1), TimeSpan.FromMinutes(10)));
    }

    private static ProcessContext KioskContext() => new()
    {
        SessionId = 1,
        KioskSessionId = 1,
        UserSid = "S-1-5-21-9",
        KioskUserSid = "S-1-5-21-9",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GameClub.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required InMemoryCafeServer Server { get; init; }
        public required LocalStore Store { get; init; }
        public required KioskController Controller { get; init; }

        public static Task<Harness> Start(long balance = 200_000)
        {
            var server = CafeKit.Server(balance);
            var store = new LocalStore(CafeKit.TempDb());
            var controller = new KioskController(new ConnectionGate(server), store, CafeKit.Station, CafeKit.Rates(), CafeKit.Hourly);
            return Task.FromResult(new Harness { Server = server, Store = store, Controller = controller });
        }

        public ValueTask DisposeAsync()
        {
            Store.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
