using GameClub.Billing;

namespace GameClub.Kiosk.Core.Tests;

public class SessionTests
{
    [Fact]
    public void Login_balance_is_shown_as_toman_and_as_time_including_the_grace_minute()
    {
        var start = CafeKit.At(10, 0);
        var session = KioskSessionMath.Begin(CafeKit.Money(3_500), "s", "رضا", 20, 1, start);
        var view = KioskSessionMath.Project(session, CafeKit.Station, start, CafeKit.Rates());

        Assert.Equal(KioskPhase.Active, view.Phase);
        Assert.Equal(3_500, view.SpendableToman);
        Assert.Equal(4 * 60, view.RemainingSeconds);
        Assert.Equal("۳٬۵۰۰ تومان", view.MoneyText);
        Assert.Equal("۴ دقیقه", view.TimeText);
        Assert.False(view.ShouldLock);
    }

    [Fact]
    public void Mid_minute_countdown_matches_the_covered_end()
    {
        var start = CafeKit.At(10, 0);
        var session = KioskSessionMath.Begin(CafeKit.Money(3_500), "s", "رضا", 20, 1, start);
        var view = KioskSessionMath.Project(session, CafeKit.Station, start.AddSeconds(30), CafeKit.Rates());

        Assert.Equal(KioskPhase.Active, view.Phase);
        Assert.Equal(2_500, view.SpendableToman);
        Assert.Equal(210, view.RemainingSeconds);
        Assert.Equal("۳ دقیقه و ۳۰ ثانیه", view.TimeText);
        Assert.Equal(2_500, view.Account!.PcMoneyToman);
    }

    [Fact]
    public void The_last_partial_minute_is_grace_and_then_the_station_locks_at_zero()
    {
        var start = CafeKit.At(10, 0);
        var rates = CafeKit.Rates();
        var session = KioskSessionMath.Begin(CafeKit.Money(3_500), "s", "رضا", 20, 1, start);

        var during = KioskSessionMath.Project(session, CafeKit.Station, start.AddSeconds(200), rates);
        Assert.Equal(KioskPhase.Grace, during.Phase);
        Assert.True(during.InGrace);
        Assert.Equal(0, during.SpendableToman);
        Assert.Equal(40, during.RemainingSeconds);
        Assert.StartsWith("مهلت:", during.TimeText);
        Assert.Equal(0, during.Account!.PcMoneyToman);

        var end = KioskSessionMath.Apply(session, KioskSessionMath.Project(session, CafeKit.Station, start.AddMinutes(4), rates), start.AddMinutes(4));
        Assert.Equal(KioskPhase.Locked, end.Phase);
        Assert.Equal(SessionEndReason.CreditExhausted, end.EndReason);
        Assert.Equal(0, end.Account!.PcMoneyToman);
        Assert.Equal(start.AddMinutes(4), end.EndedAt);

        var late = KioskSessionMath.Project(session, CafeKit.Station, start.AddMinutes(10), rates);
        Assert.True(late.ShouldLock);
        Assert.Equal(0, late.Account!.PcMoneyToman);
        Assert.Equal(start.AddMinutes(4), late.BillUntil);
    }

    [Fact]
    public void Logout_keeps_the_unspent_balance()
    {
        var start = CafeKit.At(10, 0);
        var session = KioskSessionMath.Begin(CafeKit.Money(10_000), "s", "رضا", 20, 1, start);
        var at = start.AddMinutes(3);
        var view = KioskSessionMath.Project(session, CafeKit.Station, at, CafeKit.Rates());
        var loggedOut = KioskSessionMath.Logout(session, view, at);

        Assert.Equal(SessionEndReason.UserLogout, loggedOut.EndReason);
        Assert.Equal(7_000, loggedOut.Account!.PcMoneyToman);
        Assert.True(loggedOut.Account.PcMoneyToman >= 0);
    }

    [Fact]
    public void Without_grace_the_leftover_toman_stays_on_the_account()
    {
        var start = CafeKit.At(10, 0);
        var session = KioskSessionMath.Begin(CafeKit.Money(2_500, grace: false), "s", "رضا", 20, 1, start);
        var view = KioskSessionMath.Project(session, CafeKit.Station, start.AddMinutes(3), CafeKit.Rates());

        Assert.True(view.ShouldLock);
        Assert.Equal(500, view.Account!.PcMoneyToman);
        Assert.Equal(start.AddMinutes(2), view.BillUntil);
    }

    [Fact]
    public void A_long_overrun_never_makes_the_pc_balance_negative()
    {
        var start = CafeKit.At(10, 0);
        var rates = CafeKit.Rates();
        foreach (var minutes in new[] { 0, 1, 5, 30, 180 })
        {
            var session = KioskSessionMath.Begin(CafeKit.Money(4_000), "s" + minutes, "رضا", 20, 1, start);
            var view = KioskSessionMath.Project(session, CafeKit.Station, start.AddMinutes(minutes), rates);
            Assert.True(view.Account!.PcMoneyToman >= 0);
        }
    }

    [Fact]
    public void Package_minutes_are_time_and_survive_logout()
    {
        var start = CafeKit.At(10, 0);
        var account = CafeKit.WithPackages(1_000, new TimePackage("pack", 30));
        Assert.True(PlayEligibility.Check(account, CafeKit.Station, start, CafeKit.Rates(), null, null, out _));
        var session = KioskSessionMath.Begin(account, "s", "رضا", 20, 1, start);
        var view = KioskSessionMath.Project(session, CafeKit.Station, start.AddMinutes(10), CafeKit.Rates());
        var loggedOut = KioskSessionMath.Logout(session, view, start.AddMinutes(10));

        Assert.Equal(20, loggedOut.Account!.Packages[0].RemainingMinutes);
        Assert.Equal(1_000, loggedOut.Account.PcMoneyToman);
    }

    [Fact]
    public void Zero_credit_and_shared_debt_cannot_start()
    {
        var at = CafeKit.At(10, 0);
        Assert.False(PlayEligibility.Check(CafeKit.Money(0), CafeKit.Station, at, CafeKit.Rates(), null, null, out var empty));
        Assert.Equal(PlayEligibility.Insufficient, empty);

        var shared = new CustomerAccount(
            "m",
            WalletSettings.Shared,
            new WalletPocket(-50_000, 0),
            new WalletPocket(0, 0),
            [new TimePackage("pack", 60)]);
        Assert.False(PlayEligibility.Check(shared, CafeKit.Station, at, CafeKit.Rates(), null, null, out var debt));
        Assert.Equal(PlayEligibility.DebtBlocked, debt);
    }

    [Fact]
    public void Separate_wallet_console_debt_does_not_block_pc_login()
    {
        var account = new CustomerAccount(
            "m",
            WalletSettings.Separate,
            new WalletPocket(5_000, 0),
            new WalletPocket(-80_000, 0),
            []);
        Assert.True(PlayEligibility.Check(account, CafeKit.Station, CafeKit.At(10, 0), CafeKit.Rates(), null, null, out _));
    }

    [Fact]
    public void Dust_below_one_minute_is_eligible_only_when_grace_is_on()
    {
        var at = CafeKit.At(10, 0);
        Assert.True(PlayEligibility.Check(CafeKit.Money(500), CafeKit.Station, at, CafeKit.Rates(), null, null, out _));
        Assert.False(PlayEligibility.Check(CafeKit.Money(500, grace: false), CafeKit.Station, at, CafeKit.Rates(), null, null, out _));
    }

    [Fact]
    public void Grace_is_exactly_one_minute_then_the_station_locks_at_zero()
    {
        var start = CafeKit.At(10, 0);
        var session = KioskSessionMath.Begin(CafeKit.Money(3_500), "s", "رضا", 20, 1, start);
        var rates = CafeKit.Rates();

        var before = KioskSessionMath.Project(session, CafeKit.Station, start.AddSeconds(179), rates);
        Assert.Equal(KioskPhase.Active, before.Phase);
        Assert.False(before.InGrace);

        var grace = KioskSessionMath.Project(session, CafeKit.Station, start.AddSeconds(180), rates);
        Assert.Equal(KioskPhase.Grace, grace.Phase);
        Assert.Equal(60, grace.RemainingSeconds);

        var still = KioskSessionMath.Project(session, CafeKit.Station, start.AddSeconds(239), rates);
        Assert.Equal(KioskPhase.Grace, still.Phase);
        Assert.Equal(1, still.RemainingSeconds);
        Assert.Equal(0, still.Account!.PcMoneyToman);

        var locked = KioskSessionMath.Project(session, CafeKit.Station, start.AddSeconds(240), rates);
        Assert.True(locked.ShouldLock);
        Assert.Equal(0, locked.Account!.PcMoneyToman);
        Assert.Equal(start.AddMinutes(4), locked.BillUntil);
    }

    [Fact]
    public void A_free_band_counts_down_as_free_until_then_paid_minutes()
    {
        var band = new TimeBand("morning-free", CafeKit.Station.GroupId, "رایگان", new TimeOfDay(8, 0), new TimeOfDay(9, 0));
        var rates = new RateCardBuilder()
            .AddBase(CafeKit.Station.GroupId, PlayMode.Offline, 1, CafeKit.Hourly)
            .AddBand(band)
            .AddBandRate(band.Id, CafeKit.Station.GroupId, PlayMode.Offline, 1, 0)
            .Build();
        var start = CafeKit.At(8, 15);
        var session = KioskSessionMath.Begin(CafeKit.Money(60_000), "s", "رضا", 20, 1, start);
        var view = KioskSessionMath.Project(session, CafeKit.Station, start, rates);

        Assert.Equal(KioskPhase.Active, view.Phase);
        Assert.False(view.ShouldLock);
        Assert.Equal("رایگان تا ۰۹:۰۰، سپس ۱ ساعت", view.TimeText);

        var after = KioskSessionMath.Project(session, CafeKit.Station, CafeKit.At(9, 30), rates);
        Assert.Equal(KioskPhase.Active, after.Phase);
        Assert.Equal(30 * 60, after.RemainingSeconds);
        Assert.Equal("۳۰ دقیقه", after.TimeText);
    }

    [Fact]
    public void A_free_rate_does_not_lock()
    {
        var rates = CafeKit.Rates(0);
        var start = CafeKit.At(10, 0);
        var session = KioskSessionMath.Begin(CafeKit.Money(0), "s", "رضا", 20, 1, start);
        var view = KioskSessionMath.Project(session, CafeKit.Station, start.AddHours(5), rates);
        Assert.True(view.Unlimited);
        Assert.False(view.ShouldLock);
        Assert.Equal("نامحدود", view.TimeText);
        Assert.Equal(0, view.Account!.PcMoneyToman);
    }
}
