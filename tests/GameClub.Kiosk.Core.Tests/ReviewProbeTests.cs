using GameClub.Billing;

namespace GameClub.Kiosk.Core.Tests;

/// <summary>
/// The review probes used to assert the defects. They now lock the corrected behaviour.
/// </summary>
public sealed class ReviewProbeTests
{
    private static (KioskController c, LocalStore s, string path, ConnectionGate gate, InMemoryCafeServer server) Make(long balance = 200_000)
    {
        var server = CafeKit.Server(balance);
        server.SetServerClock(CafeKit.At(18, 0));
        var gate = new ConnectionGate(server);
        var path = CafeKit.TempDb();
        var store = new LocalStore(path);
        return (new KioskController(gate, store, CafeKit.Station, CafeKit.Rates(), CafeKit.Hourly), store, path, gate, server);
    }

    [Fact]
    public async Task Probe_clock_moved_back_throws_from_tick()
    {
        var k = Make();
        Assert.True(await k.c.LoginAsync("reza", "reza-demo", CafeKit.At(18, 0)));
        var ex = Record.Exception(() => k.c.Tick(CafeKit.At(17, 59)));
        Assert.Null(ex);
        Assert.Equal(KioskPhase.Active, k.c.Screen.Phase);
        Assert.Equal(200_000, k.c.Screen.SpendableToman);
    }

    [Fact]
    public async Task Probe_restore_with_clock_before_start_throws()
    {
        var k = Make();
        Assert.True(await k.c.LoginAsync("reza", "reza-demo", CafeKit.At(18, 0)));
        k.s.Dispose();
        var store = new LocalStore(k.path);
        var c2 = new KioskController(k.gate, store, CafeKit.Station, CafeKit.Rates(), CafeKit.Hourly);
        var ex = Record.Exception(() => c2.Restore(CafeKit.At(17, 0)));
        Assert.Null(ex);
        Assert.Equal(KioskPhase.Active, c2.Screen.Phase);
        Assert.Equal(200_000, c2.Screen.SpendableToman);
        store.Dispose();
    }

    [Fact]
    public async Task Probe_power_cut_downtime_is_billed()
    {
        var k = Make(200_000);
        Assert.True(await k.c.LoginAsync("reza", "reza-demo", CafeKit.At(18, 0)));
        k.c.Tick(CafeKit.At(18, 10));
        k.s.Dispose();
        var store = new LocalStore(k.path);
        var c2 = new KioskController(k.gate, store, CafeKit.Station, CafeKit.Rates(), CafeKit.Hourly);
        c2.Restore(CafeKit.At(21, 0));
        Assert.Equal(KioskPhase.Locked, c2.Screen.Phase);
        Assert.Equal(190_000, c2.Screen.SpendableToman);
        Assert.True(c2.PendingServerLogout);
        store.Dispose();
    }

    [Fact]
    public async Task Probe_short_outage_resumes_without_billing_the_gap()
    {
        var k = Make(200_000);
        Assert.True(await k.c.LoginAsync("reza", "reza-demo", CafeKit.At(18, 0)));
        k.c.Tick(CafeKit.At(18, 10));
        k.s.Dispose();
        var store = new LocalStore(k.path);
        var c2 = new KioskController(k.gate, store, CafeKit.Station, CafeKit.Rates(), CafeKit.Hourly);
        c2.Restore(CafeKit.At(18, 18));
        Assert.Equal(KioskPhase.Active, c2.Screen.Phase);
        Assert.Equal(190_000, c2.Screen.SpendableToman);
        c2.Tick(CafeKit.At(18, 19));
        Assert.Equal(189_000, c2.Screen.SpendableToman);
        store.Dispose();
    }

    [Fact]
    public async Task Probe_offline_user_logout_is_never_retried()
    {
        var k = Make();
        Assert.True(await k.c.LoginAsync("reza", "reza-demo", CafeKit.At(18, 0)));
        k.gate.Online = false;
        k.server.SetServerClock(CafeKit.At(18, 30));
        await k.c.LogoutAsync(CafeKit.At(18, 30));
        Assert.True(k.c.PendingServerLogout);
        Assert.Equal(200_000, k.server.FindAccount("member-1")!.PcMoneyToman);
        Assert.False(await k.c.LoginAsync("reza", "reza-demo", CafeKit.At(18, 31)));
        k.gate.Online = true;
        k.server.SetServerClock(CafeKit.At(18, 30));
        Assert.True(await k.c.LoginAsync("reza", "reza-demo", CafeKit.At(18, 32)));
        Assert.Equal(170_000, k.server.FindAccount("member-1")!.PcMoneyToman);
        Assert.Equal(170_000, k.c.Session.AccountAtLogin!.PcMoneyToman);
    }

    [Fact]
    public async Task Probe_server_trusts_client_hourly_rate_and_interval()
    {
        var k = Make();
        k.server.SetServerClock(CafeKit.At(18, 0));
        Assert.True(await k.c.LoginAsync("reza", "reza-demo", CafeKit.At(17, 0)));
        k.server.SetServerClock(CafeKit.At(18, 30));
        var report = new SessionReport
        {
            SessionId = k.c.Session.SessionId!,
            AccountId = "member-1",
            LoginRevision = k.c.Session.LoginRevision,
            Station = CafeKit.Station,
            HourlyToman = 1,
            StartedAt = CafeKit.At(18, 0),
            EndedAt = CafeKit.At(20, 0),
            Reason = SessionEndReason.UserLogout,
        };
        var result = await k.server.LogoutAsync(report, default);
        Assert.True(result.Accepted);
        Assert.Equal(170_000, result.Account!.PcMoneyToman);
    }

    [Fact]
    public async Task Probe_same_member_can_open_two_stations_on_one_balance()
    {
        var server = CafeKit.Server(60_000);
        server.SetServerClock(CafeKit.At(18, 0));
        var a = await server.LoginAsync("reza", "reza-demo", "pc-01", CafeKit.At(10, 0), default);
        var b = await server.LoginAsync("reza", "reza-demo", "pc-02", CafeKit.At(18, 0), default);
        Assert.True(a.Accepted);
        Assert.False(b.Accepted);
    }

    [Fact]
    public void Probe_admin_hash_file_accepts_one_iteration()
    {
        var salt = Convert.ToBase64String(new byte[16]);
        var hash = Convert.ToBase64String(new byte[32]);
        var weak = "{\"Iterations\":1,\"Salt\":\"" + salt + "\",\"Hash\":\"" + hash + "\"}";
        Assert.Throws<InvalidOperationException>(() => Pbkdf2Hash.FromJson(weak));
        var huge = "{\"Iterations\":9000000,\"Salt\":\"" + salt + "\",\"Hash\":\"" + hash + "\"}";
        Assert.Throws<InvalidOperationException>(() => Pbkdf2Hash.FromJson(huge));
        var shortSalt = "{\"Iterations\":100000,\"Salt\":\"" + Convert.ToBase64String(new byte[8]) + "\",\"Hash\":\"" + hash + "\"}";
        Assert.Throws<InvalidOperationException>(() => Pbkdf2Hash.FromJson(shortSalt));
    }

    [Fact]
    public void Probe_guard_policy_kills_cmd_regardless_of_elevation_or_session()
    {
        var kiosk = new ProcessContext { SessionId = 2, KioskSessionId = 2, UserSid = "S-1-5-21-9", KioskUserSid = "S-1-5-21-9" };
        var admin = new ProcessContext { SessionId = 3, KioskSessionId = 2, UserSid = "S-1-5-21-1", KioskUserSid = "S-1-5-21-9" };
        var system = new ProcessContext { SessionId = 0, KioskSessionId = 2, UserSid = ProcessPolicy.SystemSid, KioskUserSid = "S-1-5-21-9" };
        Assert.True(ProcessPolicy.Evaluate("cmd", elevated: true, [], kiosk).Kill);
        Assert.False(ProcessPolicy.Evaluate("cmd", elevated: true, [], admin).Kill);
        Assert.False(ProcessPolicy.Evaluate("mmc", elevated: true, [], system).Kill);
        Assert.True(ProcessPolicy.Evaluate("rundll32", false, [], kiosk).Kill);
        Assert.True(ProcessPolicy.Evaluate("hh", false, [], kiosk).Kill);
        Assert.True(ProcessPolicy.Evaluate("msedge", false, [], kiosk).Kill);
        Assert.True(ProcessPolicy.Evaluate("notepad", false, [], kiosk).Kill);
        var renamed = kiosk with { OriginalFileName = "cmd.exe", ImagePath = @"C:\Users\kiosk\AppData\Local\Temp\x.exe" };
        Assert.True(ProcessPolicy.Evaluate("x", false, [], renamed).Kill);
    }

    [Fact]
    public void Probe_http_launch_is_allowed_as_a_game()
    {
        var games = new[] { new GameEntry { Id = "web", Name = "web", Launch = "https://example.com" } };
        var plan = GameCatalog.Prepare(games, "web", new GameFilter("pc-normal", 20), WindowsGeneration.Windows10);
        Assert.False(plan.Allowed);
        var relative = new[] { new GameEntry { Id = "rel", Name = "rel", Launch = @"Games\a.exe" } };
        Assert.False(GameCatalog.Prepare(relative, "rel", new GameFilter("pc-normal", 20), WindowsGeneration.Windows10).Allowed);
        var outside = new[] { new GameEntry { Id = "out", Name = "out", Launch = @"D:\Temp\a.exe" } };
        Assert.False(GameCatalog.Prepare(outside, "out", new GameFilter("pc-normal", 20), WindowsGeneration.Windows10).Allowed);
    }

    [Fact]
    public void Probe_guard_never_detects_hang_without_first_heartbeat()
    {
        var now = CafeKit.At(18, 0);
        var grace = GuardAdvisor.Decide(new GuardSnapshot(true, null, null, [], now), now);
        Assert.False(grace.RestartShell);
        Assert.Equal("startup_grace", grace.Reason);
        var late = GuardAdvisor.Decide(new GuardSnapshot(true, null, null, [], now.AddSeconds(-30)), now);
        Assert.True(late.RestartShell);
        Assert.Equal("heartbeat_timeout", late.Reason);
    }

    [Fact]
    public async Task Forward_clock_jump_does_not_add_play()
    {
        var k = Make();
        Assert.True(await k.c.LoginAsync("reza", "reza-demo", CafeKit.At(18, 0)));
        k.c.AdvanceMonotonic(TimeSpan.FromSeconds(1), CafeKit.At(20, 0));
        Assert.Equal(199_000, k.c.Screen.SpendableToman);
        Assert.Equal(1, k.c.PlayedSeconds);
    }

    [Fact]
    public async Task Pause_locks_the_pc_and_does_not_bill_and_is_off_unless_the_server_allows_it()
    {
        var k = Make();
        Assert.True(await k.c.LoginAsync("reza", "reza-demo", CafeKit.At(18, 0)));
        Assert.False(k.c.TryPause(CafeKit.At(18, 1)));
        await k.c.LogoutAsync(CafeKit.At(18, 1));
        k.server.AllowMemberPause(true);
        Assert.True(await k.c.LoginAsync("reza", "reza-demo", CafeKit.At(18, 2)));
        Assert.True(k.c.TryPause(CafeKit.At(18, 2)));
    }

    [Fact]
    public async Task Pause_when_allowed_freezes_time_and_blocks_launch()
    {
        var server = CafeKit.Server();
        server.SetServerClock(CafeKit.At(18, 0));
        server.AllowMemberPause(true);
        var gate = new ConnectionGate(server);
        var path = CafeKit.TempDb();
        using var store = new LocalStore(path);
        var controller = new KioskController(gate, store, CafeKit.Station, CafeKit.Rates(), CafeKit.Hourly);
        var start = CafeKit.At(18, 0);
        Assert.True(await controller.LoginAsync("reza", "reza-demo", start));
        controller.PrepareLaunch("local", WindowsGeneration.Windows10);
        Assert.True(controller.TryPause(start.AddMinutes(2)));
        Assert.Equal(KioskPhase.Paused, controller.Screen.Phase);
        Assert.Empty(controller.Screen.Games);
        Assert.Contains("local", controller.ProcessesToStop);
        Assert.False(controller.PrepareLaunch("local", WindowsGeneration.Windows10).Allowed);
        var frozen = controller.Screen.SpendableToman;
        controller.Tick(start.AddMinutes(20));
        Assert.Equal(frozen, controller.Screen.SpendableToman);
        Assert.True(controller.TryResume(start.AddMinutes(21)));
        Assert.Equal(KioskPhase.Active, controller.Screen.Phase);
    }

    [Fact]
    public async Task Server_tariff_replaces_the_flat_card_across_a_band()
    {
        var banded = new RateCardBuilder()
            .AddBand(new TimeBand("night", "pc-normal", "شب", new TimeOfDay(0, 0), new TimeOfDay(8, 0)))
            .AddBase("pc-normal", PlayMode.Offline, 1, 60_000)
            .AddBandRate("night", "pc-normal", PlayMode.Offline, 1, 30_000)
            .Build();
        var start = CafeKit.At(7, 30);
        var server = new InMemoryCafeServer(CafeKit.Station, banded, 60_000, CafeKit.DemoGames());
        server.SetServerClock(start);
        server.AddMember("reza", "reza-demo", "رضا", 20, CafeKit.Money(60_000));
        var gate = new ConnectionGate(server);
        using var store = new LocalStore(CafeKit.TempDb());
        var flat = CafeKit.Rates(60_000);
        var controller = new KioskController(gate, store, CafeKit.Station, flat, 60_000);
        Assert.True(await controller.LoginAsync("reza", "reza-demo", start));
        controller.Tick(start.AddMinutes(60));
        server.SetServerClock(start.AddMinutes(60));
        await controller.LogoutAsync(start.AddMinutes(60));
        var left = server.FindAccount("member-1")!.PcMoneyToman;
        Assert.Equal(controller.Session.Account!.PcMoneyToman, left);
        Assert.Equal(15_000, left);
    }

    [Fact]
    public void Missing_special_day_rate_falls_through_to_the_band()
    {
        var card = new RateCardBuilder()
            .AddBand(new TimeBand("night", "pc-normal", "شب", new TimeOfDay(0, 0), new TimeOfDay(8, 0)))
            .AddBase("pc-normal", PlayMode.Offline, 1, 60_000)
            .AddBandRate("night", "pc-normal", PlayMode.Offline, 1, 30_000)
            .AddWeekday(DayOfWeek.Friday, "pc-normal", PlayMode.Offline, 1, 40_000)
            .AddSpecialRate("festival", "other", PlayMode.Offline, 1, 10_000)
            .Build();
        var days = new SpecialDayCalendar(
            [SpecialDay.Once(JalaliDate.FromGregorian(new LocalDate(2026, 6, 2)), "جشن", "festival")]);
        var quote = RateResolver.Resolve(card, days, CafeKit.At(1, 0), "pc-normal", PlayMode.Offline, 1);
        Assert.Equal(RateSource.TimeBand, quote.Source);
        Assert.Equal(30_000, quote.HourlyRateToman);
    }

    [Fact]
    public void Started_minute_logout_matches_on_an_exact_line()
    {
        var start = CafeKit.At(10, 0);
        var end = start.AddMinutes(1).AddSeconds(1);
        var session = KioskSessionMath.Begin(CafeKit.Money(10_000), "s", "رضا", 20, 1, start);
        var view = KioskSessionMath.Project(session, CafeKit.Station, end, CafeKit.Rates(), BillingPolicy.Default);
        var logged = KioskSessionMath.Logout(session, view, end);
        var server = InMemoryCafeServer.Settle(CafeKit.Money(10_000), CafeKit.Station, CafeKit.Rates(), "s", start, end);
        Assert.Equal(server.Account.PcMoneyToman, logged.Account!.PcMoneyToman);
        Assert.Equal(8_000, logged.Account.PcMoneyToman);
    }

    [Fact]
    public async Task One_flush_settles_once_and_a_throwing_server_does_not_escape()
    {
        var inner = CafeKit.Server();
        inner.SetServerClock(CafeKit.At(18, 0));
        var slow = new HoldingServer(inner);
        var path = CafeKit.TempDb();
        using var store = new LocalStore(path);
        var controller = new KioskController(slow, store, CafeKit.Station, CafeKit.Rates(), CafeKit.Hourly);
        Assert.True(await controller.LoginAsync("reza", "reza-demo", CafeKit.At(18, 0)));
        inner.SetServerClock(CafeKit.At(18, 10));
        var logout = controller.LogoutAsync(CafeKit.At(18, 10));
        var second = controller.FlushAsync();
        slow.Release.Set();
        await logout;
        await second;
        Assert.Equal(1, slow.Logouts);
        Assert.Equal(190_000, inner.FindAccount("member-1")!.PcMoneyToman);

        var boom = new KioskController(new ThrowingServer(), store, CafeKit.Station, CafeKit.Rates(), CafeKit.Hourly);
        var ex = await Record.ExceptionAsync(() => boom.LoginAsync("reza", "reza-demo", CafeKit.At(19, 0)));
        Assert.Null(ex);
        Assert.Equal(KioskPhase.Locked, boom.Screen.Phase);
        Assert.True(boom.Screen.Offline);
    }

    [Fact]
    public void Browsers_are_killed_and_logged_even_when_renamed()
    {
        var kiosk = new ProcessContext { SessionId = 2, KioskSessionId = 2, UserSid = "S-1-5-21-9", KioskUserSid = "S-1-5-21-9" };
        foreach (var name in new[] { "chrome", "msedge", "firefox", "opera", "iexplore", "brave" })
        {
            var decision = ProcessPolicy.Evaluate(name, false, [], kiosk);
            Assert.True(decision.Kill);
            Assert.Equal("browser", decision.Reason);
        }

        var renamed = kiosk with { OriginalFileName = "chrome.exe", ImagePath = @"C:\Users\kiosk\AppData\Local\Temp\x.exe" };
        var copy = ProcessPolicy.Evaluate("x", false, [], renamed);
        Assert.True(copy.Kill);
        Assert.Equal("browser", copy.Reason);
        var line = GuardLog.Kill(CafeKit.At(18, 0), "x", copy.Reason);
        Assert.Contains("+03:30", line);
        Assert.Contains("killed x browser", line);
        Assert.False(ProcessPolicy.Evaluate("chrome", false, [], new ProcessContext
        {
            SessionId = 1,
            KioskSessionId = 2,
            UserSid = "S-1-5-21-1",
            KioskUserSid = "S-1-5-21-9",
        }).Kill);
    }

    [Fact]
    public void Offline_log_stores_tehran_offset_from_the_utc_instant()
    {
        using var store = new LocalStore(CafeKit.TempDb());
        var utc = new DateTimeOffset(2026, 6, 2, 14, 30, 0, TimeSpan.Zero);
        store.Append("s", OfflineEventKind.Heartbeat, utc, "beat");
        var stored = store.ReadSession("s")[0].At;
        Assert.Equal(TimeSpan.FromHours(3.5), stored.Offset);
        Assert.Equal(18, stored.Hour);
        Assert.Equal(0, stored.Minute);
    }

    [Fact]
    public async Task Absence_inside_the_timeout_resumes_without_billing_the_gap()
    {
        var k = Make();
        Assert.True(await k.c.LoginAsync("reza", "reza-demo", CafeKit.At(18, 0)));
        k.c.Tick(CafeKit.At(18, 5));
        k.s.Dispose();
        using var store = new LocalStore(k.path);
        var resumed = new KioskController(k.gate, store, CafeKit.Station, CafeKit.Rates(), CafeKit.Hourly);
        resumed.Restore(CafeKit.At(18, 12));
        Assert.Equal(KioskPhase.Active, resumed.Screen.Phase);
        Assert.Equal(195_000, resumed.Screen.SpendableToman);
    }

    [Fact]
    public async Task Absence_past_the_configured_timeout_closes_at_the_checkpoint()
    {
        var k = Make();
        Assert.True(await k.c.LoginAsync("reza", "reza-demo", CafeKit.At(18, 0)));
        k.c.Tick(CafeKit.At(18, 5));
        k.s.Dispose();
        using var store = new LocalStore(k.path);
        var closed = new KioskController(k.gate, store, CafeKit.Station, CafeKit.Rates(), CafeKit.Hourly, absenceLimit: TimeSpan.FromMinutes(1));
        closed.Restore(CafeKit.At(18, 12));
        Assert.Equal(KioskPhase.Locked, closed.Screen.Phase);
        Assert.Equal(195_000, closed.Screen.SpendableToman);
        Assert.True(closed.PendingServerLogout);
    }

    [Fact]
    public void Escape_requires_the_shell_path_and_the_password()
    {
        var hash = Pbkdf2Hash.Create("secret", iterations: Pbkdf2Hash.MinimumIterations);
        var gate = new AdminGate(hash);
        var now = CafeKit.At(9, 0);
        Assert.False(EscapeAuth.TryAuthorize(gate, "secret", @"C:\Temp\other.exe", @"C:\Program Files\GameClub\GameClub.Kiosk.exe", now, TimeSpan.FromMinutes(10), out var wrong));
        Assert.Equal("unexpected_process", wrong);
        Assert.True(EscapeAuth.TryAuthorize(gate, "secret", @"C:\Program Files\GameClub\GameClub.Kiosk.exe", @"C:\Program Files\GameClub\GameClub.Kiosk.exe", now, TimeSpan.FromMinutes(10), out var ok));
        Assert.Equal("ok", ok);
    }

    [Fact]
    public void Startup_fails_closed_without_https_and_a_pin()
    {
        var missing = KioskStartup.Decide(null, debugBuild: true, demoSwitch: false);
        Assert.False(missing.Run);
        var http = KioskStartup.Decide("{\"HubUrl\":\"http://cafe/hub\",\"CaThumbprint\":\"AB\"}", false, false);
        Assert.False(http.Run);
        var demoRelease = KioskStartup.Decide("{\"Demo\":true}", debugBuild: false, demoSwitch: false);
        Assert.False(demoRelease.Run);
        var demoDebug = KioskStartup.Decide("{\"Demo\":true}", debugBuild: true, demoSwitch: false);
        Assert.True(demoDebug.Demo);
        var ok = KioskStartup.Decide("{\"HubUrl\":\"https://cafe/hub\",\"CaThumbprint\":\"ab:cd\"}", false, false);
        Assert.True(ok.Run);
        Assert.Equal("ABCD", ok.CaThumbprint);
    }

    [Fact]
    public void Shell_close_and_interactive_session_rules()
    {
        Assert.True(ShellClosePolicy.CancelClose(allowClose: false));
        Assert.False(ShellClosePolicy.CancelClose(allowClose: true));
        Assert.True(ShellClosePolicy.SwallowAltF4(isSystemKeyF4: true, altDown: true));
        Assert.False(InteractiveSession.IsKioskShell(0, 2, "S-1", "S-1"));
        Assert.False(InteractiveSession.IsKioskShell(2, 2, ProcessPolicy.SystemSid, "S-1"));
        Assert.True(InteractiveSession.IsKioskShell(2, 2, "S-1-5-21-9", "S-1-5-21-9"));
        Assert.Equal(@"winsta0\default", InteractiveSession.Desktop);
    }

    [Fact]
    public void Tehran_projection_ignores_a_dst_offset()
    {
        var utc = new DateTimeOffset(2026, 6, 2, 6, 30, 0, TimeSpan.Zero);
        var tehran = WindowsSupport.TehranNow(utc);
        Assert.Equal(TimeSpan.FromHours(3.5), tehran.Offset);
        var dst = new DateTimeOffset(2026, 6, 2, 10, 30, 0, TimeSpan.FromHours(4.5));
        var corrected = WindowsSupport.TehranNow(dst);
        Assert.Equal(TimeSpan.FromHours(3.5), corrected.Offset);
        Assert.Equal(9, corrected.Hour);
    }

    private sealed class HoldingServer : ICafeServer
    {
        private readonly ICafeServer _inner;
        public ManualResetEventSlim Release { get; } = new(false);
        public int Logouts;

        public HoldingServer(ICafeServer inner) => _inner = inner;

        public Task<LoginResult> LoginAsync(string username, string password, string stationId, DateTimeOffset at, CancellationToken cancellationToken) =>
            _inner.LoginAsync(username, password, stationId, at, cancellationToken);

        public async Task<LogoutResult> LogoutAsync(SessionReport report, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Logouts);
            await Task.Run(() => Release.Wait(cancellationToken), cancellationToken);
            return await _inner.LogoutAsync(report, cancellationToken);
        }

        public Task<IReadOnlyList<GameEntry>> GetCatalogAsync(CancellationToken cancellationToken) =>
            _inner.GetCatalogAsync(cancellationToken);
    }

    private sealed class ThrowingServer : ICafeServer
    {
        public Task<LoginResult> LoginAsync(string username, string password, string stationId, DateTimeOffset at, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("hub down");

        public Task<LogoutResult> LogoutAsync(SessionReport report, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("hub down");

        public Task<IReadOnlyList<GameEntry>> GetCatalogAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("hub down");
    }
}
