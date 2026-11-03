using GameClub.Billing;

namespace GameClub.Kiosk.Core;

public sealed class LoginResult
{
    public bool Accepted { get; init; }

    public bool Offline { get; init; }

    public string? Message { get; init; }

    public string? SessionId { get; init; }

    public string? MemberName { get; init; }

    public int? MemberAge { get; init; }

    public long Revision { get; init; }

    public CustomerAccount? Account { get; init; }

    public long HourlyToman { get; init; }

    public DateTimeOffset ServerNow { get; init; }

    public TariffDocument? Tariff { get; init; }

    public CalendarDocument? Calendar { get; init; }

    public bool AllowPause { get; init; }

    public IReadOnlyList<GameEntry> Catalog { get; init; } = [];

    public static LoginResult Fail(string message) => new() { Message = message };

    public static LoginResult Disconnected() => new() { Offline = true, Message = "ارتباط با سرور قطع است." };
}

public sealed class LogoutResult
{
    public bool Accepted { get; init; }

    public bool Offline { get; init; }

    public CustomerAccount? Account { get; init; }

    public long Revision { get; init; }

    public string? Message { get; init; }
}

public sealed class SessionReport
{
    public string SessionId { get; init; } = "";

    public string AccountId { get; init; } = "";

    public long LoginRevision { get; init; }

    public StationContext Station { get; init; } = new("pc", "pc", PlayMode.Offline, 1);

    public long HourlyToman { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset EndedAt { get; init; }

    public SessionEndReason Reason { get; init; }
}

public interface ICafeServer
{
    /// <summary>
    /// <paramref name="at"/> is a client hint. The server records its own clock and ignores this value
    /// whenever it has one. The in-memory stand-in falls back to <paramref name="at"/> only when a test
    /// has not called <see cref="InMemoryCafeServer.SetServerClock"/>.
    /// </summary>
    Task<LoginResult> LoginAsync(string username, string password, string stationId, DateTimeOffset at, CancellationToken cancellationToken);

    Task<LogoutResult> LogoutAsync(SessionReport report, CancellationToken cancellationToken);

    Task<IReadOnlyList<GameEntry>> GetCatalogAsync(CancellationToken cancellationToken);
}

public sealed class ConnectionGate : ICafeServer
{
    private readonly ICafeServer _inner;

    public ConnectionGate(ICafeServer inner)
    {
        _inner = inner;
    }

    public bool Online { get; set; } = true;

    public Task<LoginResult> LoginAsync(string username, string password, string stationId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (!Online)
        {
            return Task.FromResult(LoginResult.Disconnected());
        }

        return _inner.LoginAsync(username, password, stationId, at, cancellationToken);
    }

    public Task<LogoutResult> LogoutAsync(SessionReport report, CancellationToken cancellationToken)
    {
        if (!Online)
        {
            return Task.FromResult(new LogoutResult { Offline = true, Message = "ارتباط با سرور قطع است." });
        }

        return _inner.LogoutAsync(report, cancellationToken);
    }

    public Task<IReadOnlyList<GameEntry>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        if (!Online)
        {
            return Task.FromResult<IReadOnlyList<GameEntry>>([]);
        }

        return _inner.GetCatalogAsync(cancellationToken);
    }
}

public sealed class InMemoryCafeServer : ICafeServer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Member> _members = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CustomerAccount> _settledSessions = new(StringComparer.Ordinal);
    private DateTimeOffset? _serverNow;
    private readonly RateCard _rates;
    private readonly StationContext _station;
    private readonly long _hourlyToman;
    private SpecialDayCalendar _specialDays = SpecialDayCalendar.Empty;
    private BillingPolicy _policy = BillingPolicy.Default;
    private bool _allowPause;
    private IReadOnlyList<GameEntry> _catalog;

    public InMemoryCafeServer(StationContext station, RateCard rates, long hourlyToman, IReadOnlyList<GameEntry> catalog)
    {
        _station = station;
        _rates = rates;
        _hourlyToman = hourlyToman;
        _catalog = GameCatalog.Normalize(catalog);
    }

    public void AddMember(string username, string password, string displayName, int? age, CustomerAccount account)
    {
        lock (_gate)
        {
            _members[username] = new Member
            {
                Password = Pbkdf2Hash.Create(password),
                DisplayName = displayName,
                Age = age,
                Account = account,
                Revision = 1,
            };
        }
    }

    public void SetServerClock(DateTimeOffset now) => _serverNow = now;

    public void UseCalendar(SpecialDayCalendar calendar) => _specialDays = calendar ?? SpecialDayCalendar.Empty;

    public void UsePolicy(BillingPolicy policy) => _policy = policy ?? BillingPolicy.Default;

    public void AllowMemberPause(bool allow) => _allowPause = allow;

    public void NoteHeartbeat(string sessionId, DateTimeOffset at)
    {
        lock (_gate)
        {
            foreach (var member in _members.Values)
            {
                if (!string.Equals(member.OpenSessionId, sessionId, StringComparison.Ordinal))
                {
                    continue;
                }

                _ = at;
                var observed = AuthoritativeNow();
                member.HeartbeatSeen = true;
                member.LastHeartbeat = observed;
                return;
            }
        }
    }

    public void ReplaceCatalog(IReadOnlyList<GameEntry> catalog)
    {
        lock (_gate)
        {
            _catalog = GameCatalog.Normalize(catalog);
        }
    }

    public CustomerAccount? FindAccount(string accountId)
    {
        lock (_gate)
        {
            foreach (var member in _members.Values)
            {
                if (string.Equals(member.Account.Id, accountId, StringComparison.Ordinal))
                {
                    return member.Account;
                }
            }
        }

        return null;
    }

    public void TopUp(string accountId, long toman)
    {
        lock (_gate)
        {
            var member = MemberByAccount(accountId);
            var target = member.Account.Settings.Mode == WalletMode.Separate ? TopUpTarget.Pc : TopUpTarget.Shared;
            member.Account = member.Account.TopUp(target, toman);
            member.Revision++;
        }
    }

    public Task<LoginResult> LoginAsync(string username, string password, string stationId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_members.TryGetValue(username ?? "", out var member) || !member.Password.Verify(password))
            {
                return Task.FromResult(LoginResult.Fail("نام کاربری یا گذرواژه نادرست است."));
            }

            if (!string.IsNullOrEmpty(member.OpenSessionId))
            {
                return Task.FromResult(LoginResult.Fail("این حساب هم‌اکنون روی سیستم دیگری باز است."));
            }

            _ = at;
            var now = AuthoritativeNow();
            if (!PlayEligibility.Check(member.Account, _station, now, _rates, _policy, _specialDays, out var message))
            {
                return Task.FromResult(LoginResult.Fail(message ?? PlayEligibility.Insufficient));
            }

            var sessionId = Guid.NewGuid().ToString("N");
            member.OpenSessionId = sessionId;
            member.OpenStationId = stationId;
            member.OpenedAt = now;
            member.HeartbeatSeen = false;
            member.LastHeartbeat = now;
            return Task.FromResult(new LoginResult
            {
                Accepted = true,
                SessionId = sessionId,
                MemberName = member.DisplayName,
                MemberAge = member.Age,
                Revision = member.Revision,
                Account = member.Account,
                HourlyToman = _hourlyToman,
                ServerNow = now,
                Tariff = _rates.ToDocument(),
                Calendar = CalendarDocument.From(_specialDays),
                AllowPause = _allowPause,
                Catalog = _catalog.ToArray(),
            });
        }
    }

    public Task<LogoutResult> LogoutAsync(SessionReport report, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_settledSessions.TryGetValue(report.SessionId, out var existing))
            {
                Member? settledOwner = null;
                try
                {
                    settledOwner = MemberByAccount(report.AccountId);
                }
                catch (InvalidOperationException)
                {
                    settledOwner = null;
                }

                return Task.FromResult(new LogoutResult
                {
                    Accepted = true,
                    Account = existing,
                    Revision = settledOwner?.Revision ?? report.LoginRevision,
                });
            }

            Member owner;
            try
            {
                owner = MemberByAccount(report.AccountId);
            }
            catch (InvalidOperationException)
            {
                return Task.FromResult(new LogoutResult { Accepted = false, Message = "unknown_account" });
            }

            if (!string.Equals(owner.OpenSessionId, report.SessionId, StringComparison.Ordinal))
            {
                return Task.FromResult(new LogoutResult { Accepted = false, Message = "unknown_session" });
            }

            _ = report.HourlyToman;
            _ = report.StartedAt;
            _ = report.EndedAt;
            var started = owner.OpenedAt;
            var ended = AuthoritativeNow();
            if (owner.HeartbeatSeen && owner.LastHeartbeat < ended)
            {
                ended = owner.LastHeartbeat;
            }

            if (ended < started)
            {
                ended = started;
            }

            var changed = owner.Revision != report.LoginRevision;
            var settled = Price(owner.Account, report.SessionId, started, ended).Account;
            owner.Account = settled;
            owner.Revision++;
            owner.OpenSessionId = null;
            owner.OpenStationId = null;
            owner.HeartbeatSeen = false;
            _settledSessions[report.SessionId] = settled;
            return Task.FromResult(new LogoutResult
            {
                Accepted = true,
                Account = settled,
                Revision = owner.Revision,
                Message = changed ? "resettled" : "server_unchanged",
            });
        }
    }

    public Task<IReadOnlyList<GameEntry>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<GameEntry>>(_catalog.ToArray());
        }
    }

    public static PcPlayResult Settle(
        CustomerAccount account,
        StationContext station,
        RateCard rates,
        string sessionId,
        DateTimeOffset started,
        DateTimeOffset ended)
    {
        var end = ended < started ? started : ended;
        var charge = SessionPricer.Price(
            new Session(sessionId, [new PlaySegment("play", started, end, station.StationId, station.GroupId, station.Mode, station.PlayerCount)]),
            rates,
            SpecialDayCalendar.Empty,
            BillingPolicy.Default);
        var play = CustomerPlay.Settle(charge, account);
        if (account.PcMoneyToman >= 0 && play.Account.PcMoneyToman < 0)
        {
            throw new BillingException("invalid_money", "PC balance went negative.");
        }

        return play;
    }

    private PcPlayResult Price(CustomerAccount account, string sessionId, DateTimeOffset started, DateTimeOffset ended)
    {
        var end = ended < started ? started : ended;
        var charge = SessionPricer.Price(
            new Session(sessionId, [new PlaySegment("play", started, end, _station.StationId, _station.GroupId, _station.Mode, _station.PlayerCount)]),
            _rates,
            _specialDays,
            _policy);
        var play = CustomerPlay.Settle(charge, account);
        if (account.PcMoneyToman >= 0 && play.Account.PcMoneyToman < 0)
        {
            throw new BillingException("invalid_money", "PC balance went negative.");
        }

        return play;
    }

    private DateTimeOffset AuthoritativeNow()
    {
        if (_serverNow is not DateTimeOffset set)
        {
            throw new InvalidOperationException("The server clock has not been set.");
        }

        return TehranTime.Ensure(set);
    }

    private Member MemberByAccount(string accountId)
    {
        foreach (var member in _members.Values)
        {
            if (string.Equals(member.Account.Id, accountId, StringComparison.Ordinal))
            {
                return member;
            }
        }

        throw new InvalidOperationException($"Unknown account '{accountId}'.");
    }

    private sealed class Member
    {
        public Pbkdf2Hash Password { get; set; } = null!;

        public string DisplayName { get; set; } = "";

        public int? Age { get; set; }

        public CustomerAccount Account { get; set; } = null!;

        public long Revision { get; set; }

        public string? OpenSessionId { get; set; }

        public string? OpenStationId { get; set; }

        public DateTimeOffset OpenedAt { get; set; }

        public bool HeartbeatSeen { get; set; }

        public DateTimeOffset LastHeartbeat { get; set; }
    }
}

public static class SessionReconciler
{
    public static LogoutResult Recompute(
        SessionReport report,
        CustomerAccount accountAtLogin,
        CustomerAccount? serverAccount,
        long serverRevision,
        RateCard serverRates,
        DateTimeOffset serverStarted,
        DateTimeOffset serverEnded)
    {
        if (serverAccount is null)
        {
            var pending = InMemoryCafeServer.Settle(accountAtLogin, report.Station, serverRates, report.SessionId, serverStarted, serverEnded);
            return new LogoutResult
            {
                Accepted = false,
                Offline = true,
                Account = pending.Account,
                Revision = report.LoginRevision,
                Message = "pending",
            };
        }

        var basis = serverRevision == report.LoginRevision ? accountAtLogin : serverAccount;
        var play = InMemoryCafeServer.Settle(basis, report.Station, serverRates, report.SessionId, serverStarted, serverEnded);
        return new LogoutResult
        {
            Accepted = true,
            Account = play.Account,
            Revision = serverRevision + 1,
            Message = serverRevision == report.LoginRevision ? "server_unchanged" : "resettled",
        };
    }
}
