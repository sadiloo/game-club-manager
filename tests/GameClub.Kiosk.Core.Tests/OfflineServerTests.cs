namespace GameClub.Kiosk.Core.Tests;

public class OfflineServerTests
{
    [Fact]
    public async Task Login_shows_the_admin_catalog_and_logout_keeps_the_balance()
    {
        await using var scope = await Harness.Start();
        var start = CafeKit.At(18, 0);
        scope.Server.SetServerClock(start);
        Assert.True(await scope.Controller.LoginAsync("reza", "reza-demo", start));
        Assert.Equal(["local", "valorant", "cs2"], scope.Controller.Screen.Games.Select(game => game.Id).ToArray());
        Assert.Equal(200_000, scope.Controller.Screen.SpendableToman);
        Assert.Contains("تومان", scope.Controller.Screen.MoneyText);

        var plan = scope.Controller.PrepareLaunch("local", WindowsGeneration.Windows10);
        Assert.True(plan.Allowed);
        Assert.False(scope.Controller.PrepareLaunch("hidden", WindowsGeneration.Windows10).Allowed);

        var at = start.AddMinutes(10);
        scope.Server.SetServerClock(at);
        await scope.Controller.LogoutAsync(at);
        Assert.Equal(KioskPhase.Locked, scope.Controller.Screen.Phase);
        Assert.Equal(190_000, scope.Server.FindAccount("member-1")!.PcMoneyToman);
        Assert.Equal(190_000, scope.Controller.Session.Account!.PcMoneyToman);
    }

    [Fact]
    public async Task Wrong_password_stays_on_the_lock_screen()
    {
        await using var scope = await Harness.Start();
        scope.Server.SetServerClock(CafeKit.At(18, 0));
        Assert.False(await scope.Controller.LoginAsync("reza", "nope", CafeKit.At(18, 0)));
        Assert.Equal(KioskPhase.Locked, scope.Controller.Screen.Phase);
        Assert.Equal(200_000, scope.Server.FindAccount("member-1")!.PcMoneyToman);
    }

    [Fact]
    public async Task Credit_hits_zero_after_grace_and_locks_without_going_negative()
    {
        await using var scope = await Harness.Start(balance: 2_500);
        var start = CafeKit.At(18, 0);
        scope.Server.SetServerClock(start);
        Assert.True(await scope.Controller.LoginAsync("reza", "reza-demo", start));
        scope.Controller.Tick(start.AddSeconds(130));
        Assert.Equal(KioskPhase.Grace, scope.Controller.Screen.Phase);

        scope.Controller.Tick(start.AddMinutes(5));
        Assert.Equal(KioskPhase.Locked, scope.Controller.Screen.Phase);
        Assert.Equal(SessionEndReason.CreditExhausted, scope.Controller.Session.EndReason);
        Assert.Equal(0, scope.Controller.Session.Account!.PcMoneyToman);
        Assert.True(scope.Controller.PendingServerLogout);

        scope.Server.SetServerClock(start.AddMinutes(5));
        await scope.Controller.FlushAsync();
        Assert.False(scope.Controller.PendingServerLogout);
        Assert.Equal(0, scope.Server.FindAccount("member-1")!.PcMoneyToman);
    }

    [Fact]
    public async Task Offline_play_keeps_counting_and_a_restart_resumes_the_same_balance()
    {
        await using var scope = await Harness.Start();
        var start = CafeKit.At(18, 0);
        scope.Server.SetServerClock(start);
        Assert.True(await scope.Controller.LoginAsync("reza", "reza-demo", start));
        scope.Gate.Online = false;
        scope.Controller.Tick(start.AddMinutes(4));
        Assert.Equal(196_000, scope.Controller.Screen.SpendableToman);
        Assert.Equal(200_000, scope.Server.FindAccount("member-1")!.PcMoneyToman);

        var resumed = scope.Resume(start.AddMinutes(4));
        Assert.Equal(KioskPhase.Active, resumed.Screen.Phase);
        Assert.Equal(196_000, resumed.Screen.SpendableToman);
        Assert.Contains(resumed.Screen.Games, game => game.Id == "cs2");

        resumed.Tick(start.AddMinutes(4).AddSeconds(30));
        Assert.Equal(195_000, resumed.Screen.SpendableToman);
    }

    [Fact]
    public async Task Reconciliation_ignores_a_tampered_balance_and_a_top_up_is_resettled()
    {
        var server = CafeKit.Server(balance: 10_000);
        var start = CafeKit.At(18, 0);
        server.SetServerClock(start);
        var login = await server.LoginAsync("reza", "reza-demo", "pc-01", start, CancellationToken.None);
        var report = new SessionReport
        {
            SessionId = login.SessionId!,
            AccountId = login.Account!.Id,
            LoginRevision = login.Revision,
            Station = CafeKit.Station,
            HourlyToman = CafeKit.Hourly,
            StartedAt = start,
            EndedAt = start.AddMinutes(3),
            Reason = SessionEndReason.UserLogout,
        };

        var honest = SessionReconciler.Recompute(report, login.Account, login.Account, login.Revision, CafeKit.Rates(), start, start.AddMinutes(3));
        Assert.Equal("server_unchanged", honest.Message);
        Assert.Equal(7_000, honest.Account!.PcMoneyToman);

        var pending = SessionReconciler.Recompute(report, login.Account, null, login.Revision, CafeKit.Rates(), start, start.AddMinutes(3));
        Assert.True(pending.Offline);
        Assert.Equal(7_000, pending.Account!.PcMoneyToman);

        server.TopUp("member-1", 20_000);
        var current = server.FindAccount("member-1")!;
        var resettled = SessionReconciler.Recompute(report, login.Account, current, login.Revision + 1, CafeKit.Rates(), start, start.AddMinutes(3));
        Assert.Equal("resettled", resettled.Message);
        Assert.Equal(27_000, resettled.Account!.PcMoneyToman);
        Assert.True(resettled.Account.PcMoneyToman >= 0);

        server.SetServerClock(start.AddMinutes(3));
        var first = await server.LogoutAsync(report, CancellationToken.None);
        var second = await server.LogoutAsync(report, CancellationToken.None);
        Assert.Equal("resettled", first.Message);
        Assert.Equal(first.Account!.PcMoneyToman, second.Account!.PcMoneyToman);
        Assert.Equal(27_000, server.FindAccount("member-1")!.PcMoneyToman);
    }

    [Fact]
    public async Task Catalog_roundtrip_and_pipe_frames_survive_a_partial_buffer()
    {
        var path = CafeKit.TempDb();
        var games = CafeKit.DemoGames();
        using (var store = new LocalStore(path))
        {
            store.SaveCatalog(games, CafeKit.At(1, 0));
            store.Append("s", OfflineEventKind.Login, CafeKit.At(1, 0), "login");
            store.Append("s", OfflineEventKind.Heartbeat, CafeKit.At(1, 1), "beat");
        }

        using var again = new LocalStore(path);
        var cached = again.ReadCatalog();
        Assert.Equal("cs2", cached[0].Id);
        Assert.Equal("covers/cs2.png", cached[0].CoverPath);
        Assert.Equal("steam://rungameid/730", cached[0].Launch);
        Assert.Contains("vgc", cached.Single(game => game.Id == "valorant").HelperProcesses);
        var events = again.ReadSession("s");
        Assert.Equal(2, events.Count);
        Assert.False(events[0].Synced);
        again.MarkSynced(events.Select(entry => entry.Id));
        Assert.Empty(again.Unsynced());

        var packet = PipeFraming.Encode(new PipeMessage { Kind = "heartbeat", SessionId = "s", UnixMilliseconds = 5 });
        var partial = packet.Take(3).ToArray();
        Assert.False(PipeFraming.TryDecode(partial, partial.Length, out _, out _));
        Assert.True(PipeFraming.TryDecode(packet, packet.Length, out var message, out var consumed));
        Assert.Equal(packet.Length, consumed);
        Assert.Equal("heartbeat", message!.Kind);
        Assert.Equal("s", message.SessionId);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required InMemoryCafeServer Server { get; init; }
        public required ConnectionGate Gate { get; init; }
        public required LocalStore Store { get; init; }
        public required KioskController Controller { get; init; }
        public required string Path { get; init; }

        public static Task<Harness> Start(long balance = 200_000)
        {
            var server = CafeKit.Server(balance);
            var gate = new ConnectionGate(server);
            var path = CafeKit.TempDb();
            var store = new LocalStore(path);
            var controller = new KioskController(gate, store, CafeKit.Station, CafeKit.Rates(), CafeKit.Hourly);
            return Task.FromResult(new Harness
            {
                Server = server,
                Gate = gate,
                Store = store,
                Controller = controller,
                Path = path,
            });
        }

        public KioskController Resume(DateTimeOffset now)
        {
            var store = new LocalStore(Path);
            var controller = new KioskController(Gate, store, CafeKit.Station, CafeKit.Rates(), CafeKit.Hourly);
            controller.Restore(now);
            return controller;
        }

        public ValueTask DisposeAsync()
        {
            Store.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
