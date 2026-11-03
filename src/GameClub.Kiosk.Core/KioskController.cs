using GameClub.Billing;

namespace GameClub.Kiosk.Core;

public sealed class KioskScreen
{
    public KioskPhase Phase { get; init; }

    public string? Message { get; init; }

    public string MemberName { get; init; } = "";

    public string MoneyText { get; init; } = "";

    public string TimeText { get; init; } = "";

    public bool InGrace { get; init; }

    public bool Offline { get; init; }

    public string? ActiveGameId { get; init; }

    public long SpendableToman { get; init; }

    public long? RemainingSeconds { get; init; }

    public IReadOnlyList<LauncherGame> Games { get; init; } = [];
}

public sealed class KioskController
{
    private readonly ICafeServer _server;
    private readonly LocalStore _store;
    private readonly StationContext _station;
    private readonly BillingPolicy _policy;
    private RateCard _rates;
    private long _hourlyToman;
    private IReadOnlyList<GameEntry> _catalog = [];
    private KioskSession _session = KioskSession.Locked();
    private DateTimeOffset _clock;
    private bool _offline;

    public KioskController(ICafeServer server, LocalStore store, StationContext station, RateCard rates, long hourlyToman, BillingPolicy? policy = null)
    {
        _server = server;
        _store = store;
        _station = station;
        _rates = rates;
        _hourlyToman = hourlyToman;
        _policy = policy ?? BillingPolicy.Default;
        Screen = LockScreen(null);
    }

    public KioskScreen Screen { get; private set; }

    public bool PendingServerLogout { get; private set; }

    public KioskSession Session => _session;

    public IReadOnlyList<GameEntry> Catalog => _catalog;

    public async Task<bool> LoginAsync(string username, string password, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (_session.IsOpen)
        {
            return false;
        }

        var result = await _server.LoginAsync(username, password, _station.StationId, now, cancellationToken).ConfigureAwait(false);
        _offline = result.Offline;
        if (!result.Accepted || result.Account is null || result.SessionId is null)
        {
            Screen = LockScreen(result.Message);
            return false;
        }

        if (!PlayEligibility.Check(result.Account, _station, now, CardFor(result.HourlyToman), _policy, SpecialDayCalendar.Empty, out var message))
        {
            await _server.LogoutAsync(Report(result.SessionId, result.Account, result.Revision, now, now, SessionEndReason.UserLogout, result.HourlyToman), cancellationToken).ConfigureAwait(false);
            Screen = LockScreen(message);
            return false;
        }

        _hourlyToman = result.HourlyToman;
        _rates = CardFor(result.HourlyToman);
        _catalog = result.Catalog ?? [];
        _store.SaveCatalog(_catalog, now);
        _session = KioskSessionMath.Begin(result.Account, result.SessionId, result.MemberName ?? username, result.MemberAge, result.Revision, now);
        PersistOpen(now);
        _store.Append(result.SessionId, OfflineEventKind.Login, now, AccountCodec.Write(AccountCodec.From(result.Account)));
        _clock = now;
        Publish();
        return true;
    }

    public void Tick(DateTimeOffset now)
    {
        if (!_session.IsOpen)
        {
            return;
        }

        _clock = now;
        var projection = KioskSessionMath.Project(_session, _station, now, _rates, _policy, SpecialDayCalendar.Empty);
        var wasOpen = _session.IsOpen;
        _session = KioskSessionMath.Apply(_session, projection, now);
        if (wasOpen && !_session.IsOpen)
        {
            PendingServerLogout = true;
            CloseLocal(projection, OfflineEventKind.Lockout);
        }
        else if (_session.SessionId is not null)
        {
            _store.Append(_session.SessionId, OfflineEventKind.Heartbeat, now, projection.SpendableToman.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Publish();
    }

    public async Task LogoutAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (!_session.IsOpen || _session.AccountAtLogin is null)
        {
            return;
        }

        _clock = now;
        var projection = KioskSessionMath.Project(_session, _station, now, _rates, _policy, SpecialDayCalendar.Empty);
        _session = KioskSessionMath.Logout(_session, projection, now);
        CloseLocal(projection, _session.EndReason == SessionEndReason.CreditExhausted ? OfflineEventKind.Lockout : OfflineEventKind.Logout);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        Publish();
    }

    public LaunchPlan PrepareLaunch(string gameId, WindowsGeneration operatingSystem)
    {
        if (!_session.IsOpen)
        {
            return new LaunchPlan(false, "ابتدا وارد شوید.", null, null, null, false);
        }

        var plan = GameCatalog.Prepare(_catalog, gameId, new GameFilter(_station.GroupId, _session.MemberAge), operatingSystem);
        if (plan.Allowed && _session.SessionId is not null)
        {
            _session = _session with { ActiveGameId = gameId };
            PersistOpen(_session.StartedAt ?? DateTimeOffset.UtcNow);
            _store.Append(_session.SessionId, OfflineEventKind.GameLaunch, _clock == default ? _session.StartedAt ?? DateTimeOffset.UtcNow : _clock, gameId);
            Publish();
        }

        return plan;
    }

    public void Restore(DateTimeOffset now)
    {
        _catalog = _store.ReadCatalog();
        var open = _store.ReadOpenSession();
        if (open?.AccountAtLogin is null)
        {
            _session = KioskSession.Locked();
            Screen = LockScreen(null);
            return;
        }

        _hourlyToman = open.HourlyToman;
        _rates = CardFor(open.HourlyToman);
        _session = KioskSessionMath.Begin(
            AccountCodec.ToAccount(open.AccountAtLogin),
            open.SessionId,
            open.MemberName,
            open.MemberAge,
            open.LoginRevision,
            DateTimeOffset.Parse(open.StartedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind));
        if (!string.IsNullOrEmpty(open.ActiveGameId))
        {
            _session = _session with { ActiveGameId = open.ActiveGameId };
        }

        Tick(now);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_session.IsOpen || _session.Account is null || _session.SessionId is null || _session.StartedAt is null || _session.EndedAt is null || _session.AccountAtLogin is null)
        {
            return;
        }

        var report = Report(
            _session.SessionId,
            _session.AccountAtLogin,
            _session.LoginRevision,
            _session.StartedAt.Value,
            _session.EndedAt.Value,
            _session.EndReason,
            _hourlyToman);
        var result = await _server.LogoutAsync(report, cancellationToken).ConfigureAwait(false);
        _offline = result.Offline;
        if (result.Accepted && result.Account is not null)
        {
            PendingServerLogout = false;
            _session = _session with { Account = result.Account, LoginRevision = result.Revision };
            var ids = new List<long>();
            foreach (var entry in _store.ReadSession(_session.SessionId))
            {
                ids.Add(entry.Id);
            }

            _store.MarkSynced(ids);
        }
    }

    private void CloseLocal(KioskProjection projection, OfflineEventKind kind)
    {
        if (_session.SessionId is not null)
        {
            var payload = _session.Account is null ? "" : AccountCodec.Write(AccountCodec.From(_session.Account));
            _store.Append(_session.SessionId, kind, _session.EndedAt ?? DateTimeOffset.UtcNow, payload);
        }

        _store.ClearOpenSession();
        _ = projection;
    }

    private void PersistOpen(DateTimeOffset now)
    {
        if (_session.AccountAtLogin is null || _session.SessionId is null || _session.StartedAt is null)
        {
            return;
        }

        _store.SaveOpenSession(new OpenSessionSnapshot
        {
            SessionId = _session.SessionId,
            MemberName = _session.MemberName ?? "",
            MemberAge = _session.MemberAge,
            LoginRevision = _session.LoginRevision,
            StartedAt = _session.StartedAt.Value.ToString("o"),
            ActiveGameId = _session.ActiveGameId,
            AccountAtLogin = AccountCodec.From(_session.AccountAtLogin),
            StationId = _station.StationId,
            GroupId = _station.GroupId,
            Mode = (int)_station.Mode,
            PlayerCount = _station.PlayerCount,
            HourlyToman = _hourlyToman,
        });
        _ = now;
    }

    private void Publish()
    {
        if (!_session.IsOpen)
        {
            var spendable = _session.Account is null ? 0 : Math.Max(0, _session.Account.PcMoneyToman);
            Screen = new KioskScreen
            {
                Phase = KioskPhase.Locked,
                Message = _session.Message,
                MemberName = _session.MemberName ?? "",
                MoneyText = PersianFormat.Money(spendable),
                TimeText = PersianFormat.Time(0, false),
                SpendableToman = spendable,
                Offline = _offline,
                Games = [],
            };
            return;
        }

        var projection = KioskSessionMath.Project(_session, _station, _clock, _rates, _policy, SpecialDayCalendar.Empty);
        Screen = new KioskScreen
        {
            Phase = _session.Phase,
            Message = _session.Phase == KioskPhase.Grace ? "مهلت یک دقیقه" : null,
            MemberName = _session.MemberName ?? "",
            MoneyText = projection.MoneyText,
            TimeText = projection.TimeText,
            InGrace = projection.InGrace,
            Offline = _offline,
            ActiveGameId = _session.ActiveGameId,
            SpendableToman = projection.SpendableToman,
            RemainingSeconds = projection.RemainingSeconds,
            Games = GameCatalog.Visible(_catalog, new GameFilter(_station.GroupId, _session.MemberAge)),
        };
    }

    private KioskScreen LockScreen(string? message) => new()
    {
        Phase = KioskPhase.Locked,
        Message = message,
        MoneyText = PersianFormat.Money(0),
        TimeText = PersianFormat.Time(0, false),
        Offline = _offline,
    };

    private SessionReport Report(
        string sessionId,
        CustomerAccount login,
        long revision,
        DateTimeOffset started,
        DateTimeOffset ended,
        SessionEndReason reason,
        long hourly) => new()
    {
        SessionId = sessionId,
        AccountId = login.Id,
        LoginRevision = revision,
        Station = _station,
        HourlyToman = hourly,
        StartedAt = started,
        EndedAt = ended,
        Reason = reason,
    };

    private RateCard CardFor(long hourly) =>
        new RateCardBuilder()
            .AddBase(_station.GroupId, _station.Mode, _station.Mode == PlayMode.Offline ? _station.PlayerCount : null, hourly)
            .Build();
}
