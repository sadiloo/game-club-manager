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
    private readonly FlushGate _flush = new();
    private readonly object _db = new();
    private readonly List<string> _processesToStop = [];
    private RateCard _rates;
    private SpecialDayCalendar _specialDays;
    private readonly TimeSpan _absenceLimit;
    private long _hourlyToman;
    private IReadOnlyList<GameEntry> _catalog = [];
    private KioskSession _session = KioskSession.Locked();
    private DateTimeOffset _clock;
    private DateTimeOffset _lastWall;
    private long _playedSeconds;
    private long _persistedPlayed = -1;
    private bool _offline;

    public KioskController(
        ICafeServer server,
        LocalStore store,
        StationContext station,
        RateCard rates,
        long hourlyToman,
        BillingPolicy? policy = null,
        SpecialDayCalendar? specialDays = null,
        TimeSpan? absenceLimit = null)
    {
        _server = server;
        _store = store;
        _station = station;
        _rates = rates;
        _hourlyToman = hourlyToman;
        _policy = policy ?? BillingPolicy.Default;
        _absenceLimit = absenceLimit ?? KioskTiming.PowerCutGap;
        _specialDays = specialDays ?? SpecialDayCalendar.Empty;
        Screen = LockScreen(null);
    }

    public KioskScreen Screen { get; private set; }

    public bool PendingServerLogout { get; private set; }

    public KioskSession Session => _session;

    public IReadOnlyList<GameEntry> Catalog => _catalog;

    public IReadOnlyList<string> ProcessesToStop => _processesToStop;

    public long PlayedSeconds => _playedSeconds;

    public async Task<bool> LoginAsync(string username, string password, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_db)
        {
            if (_session.IsOpen)
            {
                return false;
            }
        }

        await FlushAsync(cancellationToken, ignoreBackoff: true).ConfigureAwait(false);
        lock (_db)
        {
            if (_store.HasPending())
            {
                PendingServerLogout = true;
                _offline = true;
                Screen = LockScreen("خروج قبلی هنوز با سرور تسویه نشده است.");
                return false;
            }
        }

        LoginResult result;
        try
        {
            result = await _server.LoginAsync(username, password, _station.StationId, now, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            result = LoginResult.Disconnected();
        }

        LoginResult? ineligible = null;
        var ineligibleAt = default(DateTimeOffset);
        lock (_db)
        {
            _offline = result.Offline;
            if (!result.Accepted || result.Account is null || result.SessionId is null)
            {
                Screen = LockScreen(result.Message);
                return false;
            }

            AdoptServerTariff(result);
            var started = result.ServerNow != default ? result.ServerNow : now;
            if (!PlayEligibility.Check(result.Account, _station, started, _rates, _policy, _specialDays, out var message))
            {
                Screen = LockScreen(message);
                ineligible = result;
                ineligibleAt = started;
            }
            else
            {
                _hourlyToman = result.HourlyToman > 0 ? result.HourlyToman : _hourlyToman;
                _catalog = result.Catalog ?? [];
                _store.SaveCatalog(_catalog, now);
                _session = KioskSessionMath.Begin(result.Account, result.SessionId, result.MemberName ?? username, result.MemberAge, result.Revision, started);
                _playedSeconds = 0;
                _persistedPlayed = -1;
                _lastWall = now;
                _clock = now;
                _processesToStop.Clear();
            _store.ClearStopList();
                PersistOpen(now);
                _store.Append(result.SessionId, OfflineEventKind.Login, now, AccountCodec.Write(AccountCodec.From(result.Account)));
                Publish();
                return true;
            }
        }

        if (ineligible is not null)
        {
            await RejectIneligible(ineligible, ineligibleAt, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public void Tick(DateTimeOffset now)
    {
        lock (_db)
        {
            Advance(now);
        }
    }

    public async Task LogoutAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_db)
        {
            if (!_session.IsOpen || _session.AccountAtLogin is null)
            {
                return;
            }

            Advance(now);
            CloseAtPlayed(now, userLogout: true);
        }

        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Add play from a monotonic counter. <paramref name="serverNow"/> is the server's
    /// Tehran instant (UTC plus the fixed +03:30), not the PC's local clock.
    /// A jump in the wall clock does not add play.
    /// </summary>
    public void AdvanceMonotonic(TimeSpan elapsed, DateTimeOffset serverNow)
    {
        lock (_db)
        {
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            if (!_session.IsOpen || _session.StartedAt is null)
            {
                _clock = serverNow;
                return;
            }

            var seconds = (long)Math.Floor(elapsed.TotalSeconds);
            if (seconds > 0)
            {
                _playedSeconds += seconds;
            }

            _lastWall = serverNow;
            _clock = serverNow;
            ApplyProjection(serverNow);
            if (seconds > 0 && _session.IsOpen && (_playedSeconds - _persistedPlayed >= (long)KioskTiming.CheckpointInterval.TotalSeconds || seconds >= (long)KioskTiming.CheckpointInterval.TotalSeconds))
            {
                PersistCheckpoint(serverNow);
            }
        }
    }

    public LaunchPlan PrepareLaunch(string gameId, WindowsGeneration operatingSystem)
    {
        lock (_db)
        {
            if (!_session.IsOpen)
            {
                return new LaunchPlan(false, "ابتدا وارد شوید.", null, null, null, false);
            }

            var plan = GameCatalog.Prepare(_catalog, gameId, new GameFilter(_station.GroupId, _session.MemberAge), operatingSystem);
            if (plan.Allowed && _session.SessionId is not null)
            {
                _session = _session with { ActiveGameId = gameId };
                PersistOpen(_lastWall == default ? _session.StartedAt ?? DateTimeOffset.UtcNow : _lastWall);
                _store.Append(_session.SessionId, OfflineEventKind.GameLaunch, _clock == default ? _session.StartedAt ?? DateTimeOffset.UtcNow : _clock, gameId);
                Publish();
            }

            return plan;
        }
    }

    public void Restore(DateTimeOffset now)
    {
        lock (_db)
        {
            _catalog = _store.ReadCatalog();
            PendingServerLogout = _store.HasPending();
            var open = _store.ReadOpenSession();
            if (open?.AccountAtLogin is null)
            {
                _session = KioskSession.Locked();
                _playedSeconds = 0;
                Screen = LockScreen(PendingServerLogout ? "خروج قبلی هنوز با سرور تسویه نشده است." : null);
                return;
            }

            _hourlyToman = open.HourlyToman;
            var started = DateTimeOffset.Parse(open.StartedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
            _session = KioskSessionMath.Begin(
                AccountCodec.ToAccount(open.AccountAtLogin),
                open.SessionId,
                open.MemberName,
                open.MemberAge,
                open.LoginRevision,
                started);
            if (!string.IsNullOrEmpty(open.ActiveGameId))
            {
                _session = _session with { ActiveGameId = open.ActiveGameId };
            }

            _playedSeconds = open.PlayedSeconds < 0 ? 0 : open.PlayedSeconds;
            _persistedPlayed = _playedSeconds;
            _lastWall = ParseCheckpoint(open.LastCheckpointAt) ?? started;
            _clock = _lastWall;

            if (now < started)
            {
                Publish();
                return;
            }

            var gap = now - _lastWall;
            if (gap > _absenceLimit)
            {
                RememberStop(_session.ActiveGameId);
                CloseAtPlayed(now, userLogout: false, powerLoss: true);
                return;
            }

            _lastWall = now;
            _clock = now;
            ApplyProjection(now);
            PersistOpen(now);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default, bool ignoreBackoff = false)
    {
        if (!_flush.TryEnter())
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                PendingReport? pending;
                lock (_db)
                {
                    pending = _store.PeekPending();
                }

                if (pending is null)
                {
                    lock (_db)
                    {
                        PendingServerLogout = false;
                    }

                    return;
                }

                if (!ignoreBackoff
                    && pending.NextAttemptAt is string nextText
                    && DateTimeOffset.TryParse(nextText, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var next)
                    && DateTimeOffset.UtcNow < next)
                {
                    lock (_db)
                    {
                        PendingServerLogout = true;
                        _offline = true;
                    }

                    return;
                }

                var report = ToReport(pending);
                LogoutResult result;
                try
                {
                    result = await _server.LogoutAsync(report, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    result = new LogoutResult { Offline = true, Message = "ارتباط با سرور قطع است." };
                }

                lock (_db)
                {
                    _offline = result.Offline;
                    if (result.Accepted && result.Account is not null && !result.Offline)
                    {
                        _store.DeletePending(pending.SessionId);
                        _store.MarkSessionSynced(pending.SessionId);
                        if (string.Equals(_session.SessionId, pending.SessionId, StringComparison.Ordinal))
                        {
                            _session = _session with { Account = result.Account, LoginRevision = result.Revision };
                        }

                        PendingServerLogout = _store.HasPending();
                        if (!_session.IsOpen)
                        {
                            Publish();
                        }

                        continue;
                    }

                    pending.Attempts++;
                    pending.NextAttemptAt = DateTimeOffset.UtcNow.Add(KioskTiming.FlushBackoff(pending.Attempts)).ToString("o");
                    _store.SavePending(pending);
                    PendingServerLogout = true;
                    _offline = true;
                    return;
                }
            }
        }
        finally
        {
            _flush.Exit();
        }
    }

    private void Advance(DateTimeOffset now)
    {
        if (!_session.IsOpen || _session.StartedAt is null)
        {
            return;
        }

        if (_lastWall == default)
        {
            _lastWall = _session.StartedAt.Value;
        }

        if (now < _lastWall)
        {
            _clock = _lastWall;
            Publish();
            return;
        }

        var delta = (long)Math.Floor((now - _lastWall).TotalSeconds);
        if (delta < 0)
        {
            delta = 0;
        }

        _lastWall = now;
        _clock = now;
        _playedSeconds += delta;
        ApplyProjection(now);
        if (delta > 0 && _session.IsOpen && (_playedSeconds - _persistedPlayed >= (long)KioskTiming.CheckpointInterval.TotalSeconds || delta >= (long)KioskTiming.CheckpointInterval.TotalSeconds))
        {
            PersistCheckpoint(now);
        }
    }

    private void ApplyProjection(DateTimeOffset now)
    {
        if (!_session.IsOpen || _session.StartedAt is null)
        {
            return;
        }

        var gameId = _session.ActiveGameId;
        var billAt = TehranTime.AddSeconds(_session.StartedAt.Value, _playedSeconds);
        var projection = KioskSessionMath.Project(_session, _station, billAt, _rates, _policy, _specialDays);
        var wasOpen = _session.IsOpen;
        _session = KioskSessionMath.Apply(_session, projection, billAt);
        if (wasOpen && !_session.IsOpen)
        {
            RememberStop(gameId);
            CloseLocal(projection, OfflineEventKind.Lockout);
            Enqueue(projection, now);
        }

        Publish();
    }

    private void CloseAtPlayed(DateTimeOffset now, bool userLogout, bool powerLoss = false)
    {
        if (!_session.IsOpen || _session.StartedAt is null)
        {
            return;
        }

        var gameId = _session.ActiveGameId;
        var billAt = TehranTime.AddSeconds(_session.StartedAt.Value, _playedSeconds);
        var projection = KioskSessionMath.Project(_session, _station, billAt, _rates, _policy, _specialDays);
        RememberStop(gameId);
        _session = KioskSessionMath.Logout(_session, projection, billAt);
        if (powerLoss && _session.EndReason != SessionEndReason.CreditExhausted)
        {
            _session = _session with { Message = "قطع برق — جلسه تا آخرین نقطهٔ ذخیره‌شده بسته شد." };
        }
        else if (userLogout && _session.EndReason != SessionEndReason.CreditExhausted)
        {
            _session = _session with { Message = null };
        }

        CloseLocal(projection, _session.EndReason == SessionEndReason.CreditExhausted ? OfflineEventKind.Lockout : OfflineEventKind.Logout);
        Enqueue(projection, now);
        Publish();
    }

    private void Enqueue(KioskProjection projection, DateTimeOffset now)
    {
        if (_session.SessionId is null || _session.AccountAtLogin is null || _session.StartedAt is null)
        {
            return;
        }

        var ended = _session.EndedAt ?? projection.BillUntil ?? _session.StartedAt.Value;
        _store.SavePending(new PendingReport
        {
            SessionId = _session.SessionId,
            AccountId = _session.AccountAtLogin.Id,
            LoginRevision = _session.LoginRevision,
            StationId = _station.StationId,
            GroupId = _station.GroupId,
            Mode = (int)_station.Mode,
            PlayerCount = _station.PlayerCount,
            HourlyToman = _hourlyToman,
            StartedAt = _session.StartedAt.Value.ToString("o"),
            EndedAt = ended.ToString("o"),
            Reason = (int)_session.EndReason,
            Attempts = 0,
            NextAttemptAt = TehranStamp.Format(now),
            MemberName = _session.MemberName ?? "",
        });
        PendingServerLogout = true;
    }

    private void RememberStop(string? id = null)
    {
        if (id is null || _catalog.Count == 0)
        {
            return;
        }

        GameEntry? game = null;
        if (id is not null)
        {
            foreach (var entry in _catalog)
            {
                if (string.Equals(entry.Id, id, StringComparison.Ordinal))
                {
                    game = entry;
                    break;
                }
            }
        }

        if (game is null)
        {
            return;
        }

        foreach (var name in GameProcesses.Names(game))
        {
            var exists = false;
            foreach (var already in _processesToStop)
            {
                if (string.Equals(already, name, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                _processesToStop.Add(name);
            }
        }

        _store.SaveStopList(_processesToStop);
    }

    private void PersistCheckpoint(DateTimeOffset now)
    {
        if (_session.SessionId is null)
        {
            return;
        }

        _store.WriteCheckpoint(_session.SessionId, now, _playedSeconds);
        PersistOpen(now);
        _persistedPlayed = _playedSeconds;
    }

    private async Task RejectIneligible(LoginResult result, DateTimeOffset started, CancellationToken cancellationToken)
    {
        try
        {
            await _server.LogoutAsync(
                Report(result.SessionId!, result.Account!, result.Revision, started, started, SessionEndReason.UserLogout, result.HourlyToman),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The server never opened a session when eligibility fails first. A later outage is not a crash.
        }
    }

    private void CloseLocal(KioskProjection projection, OfflineEventKind kind)
    {
        if (_session.SessionId is not null)
        {
            var payload = _session.Account is null ? "" : AccountCodec.Write(AccountCodec.From(_session.Account));
            _store.Append(_session.SessionId, kind, _session.EndedAt ?? projection.BillUntil ?? DateTimeOffset.UtcNow, payload);
        }

        _store.ClearOpenSession();
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
            PlayedSeconds = _playedSeconds,
            LastCheckpointAt = now.ToString("o"),
        });
        _persistedPlayed = _playedSeconds;
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

        var billAt = _session.StartedAt is DateTimeOffset started
            ? TehranTime.AddSeconds(started, _playedSeconds)
            : _clock;
        var projection = KioskSessionMath.Project(_session, _station, billAt, _rates, _policy, _specialDays);
        Screen = new KioskScreen
        {
            Phase = _session.Phase,
            Message = _session.Phase == KioskPhase.Grace ? "مهلت یک دقیقه" : null,
            Games = GameCatalog.Visible(_catalog, new GameFilter(_station.GroupId, _session.MemberAge)),
            MemberName = _session.MemberName ?? "",
            MoneyText = projection.MoneyText,
            TimeText = projection.TimeText,
            InGrace = projection.InGrace,
            Offline = _offline,
            ActiveGameId = _session.ActiveGameId,
            SpendableToman = projection.SpendableToman,
            RemainingSeconds = projection.RemainingSeconds,
        };
    }

    private void AdoptServerTariff(LoginResult result)
    {
        if (result.Tariff is not null)
        {
            _rates = result.Tariff.ToCard();
        }

        if (result.Calendar is not null)
        {
            _specialDays = result.Calendar.ToCalendar();
        }

    }

    private KioskScreen LockScreen(string? message) => new()
    {
        Phase = KioskPhase.Locked,
        Message = message,
        MoneyText = PersianFormat.Money(0),
        TimeText = PersianFormat.Time(0, false),
        Offline = _offline,
    };

    private SessionReport ToReport(PendingReport pending)
    {
        var started = DateTimeOffset.Parse(pending.StartedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        var ended = DateTimeOffset.Parse(pending.EndedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        return new SessionReport
        {
            SessionId = pending.SessionId,
            AccountId = pending.AccountId,
            LoginRevision = pending.LoginRevision,
            Station = new StationContext(pending.StationId, pending.GroupId, (PlayMode)pending.Mode, pending.PlayerCount),
            HourlyToman = pending.HourlyToman,
            StartedAt = started,
            EndedAt = ended,
            Reason = (SessionEndReason)pending.Reason,
        };
    }

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

    private static DateTimeOffset? ParseCheckpoint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
    }
}
