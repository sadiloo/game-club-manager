using GameClub.Billing;

namespace GameClub.Billing.Tests;

/// <summary>B2 freeze examples 1–7 and 28% Titan (hand oracles in comments).</summary>
public sealed class B2BurntAfterGraceTests
{
    static CustomerAccount Wallet(long balance)
        => new("m", WalletSettings.Shared, new WalletPocket(balance, 0), new WalletPocket(0, 0), []);

    static CustomerAccount DiscountWallet(long balance)
        => new("m", WalletSettings.Shared with { ApplyDiscountsToPc = true }, new WalletPocket(balance, 0), new WalletPocket(0, 0), []);

    static SessionCharge PlayPc(
        DateTimeOffset start,
        DateTimeOffset end,
        string groupId = PilotSeed.PcNormalGroupId)
    {
        return SessionPricer.Price(
            new Session("b2", [new PlaySegment("a", start, end, "pc-1", groupId, PlayMode.Offline, 1, StationKind.Pc)]),
            PilotSeed.CreateCard());
    }

    static void AssertBurnt(PcPlayResult play, long grace, long writeOff, long consumed)
    {
        Assert.Equal(grace, play.Settlement.Grace!.WaivedToman);
        Assert.Equal(writeOff, play.Settlement.WriteOffToman);
        Assert.Equal(consumed, play.Settlement.MoneyConsumedToman);
        Assert.Equal(0, play.Account.PcMoneyToman);
        if (writeOff > 0)
        {
            Assert.Equal(writeOff, play.Settlement.Alert!.AmountToman);
            Assert.Equal("pc_overshoot", play.Settlement.Alert.Code);
            Assert.Equal(writeOff, play.Settlement.ReportEntry!.AmountToman);
        }
        else
        {
            Assert.Null(play.Settlement.Alert);
            Assert.Null(play.Settlement.ReportEntry);
            Assert.DoesNotContain(play.ChargeInvoice!.Lines, line => line.Kind == InvoiceLineKind.WriteOff);
        }
    }

    [Fact]
    public void b2_example_1_eighty_thousand_180s_balance_1833_retained_wallet_covers_post_grace()
    {
        // Daytime 14:00 — pins 80k/h (not night band 100k). Marginals: 1333+1334+1333=4000 gross.
        // m1 pay 1333 → bal 500; grace m2 waive 1334, bal 500; m3 pay 500, burnt 833.
        var start = T.At(2026, 6, 3, 14, 0);
        var play = CustomerPlay.Settle(PlayPc(start, start.AddSeconds(180)), Wallet(1_833));
        AssertBurnt(play, 1_334, 833, 1_833);
        Assert.Equal(1_833, play.ChargeInvoice!.PayableToman);
        Assert.Equal(4_000, play.ChargeInvoice.TimeGrossToman);
    }

    [Fact]
    public void b2_example_2_night_100k_180s_2000()
    {
        // 02:00 in pc-normal special band → 100k/h. Marginals 1667+1666+1667=5000.
        // m1 1667 → bal 333; grace waive 1666; m3 pay 333, burnt 1334.
        var start = T.At(2026, 6, 3, 2, 0);
        var play = CustomerPlay.Settle(PlayPc(start, start.AddSeconds(180)), Wallet(2_000));
        AssertBurnt(play, 1_666, 1_334, 2_000);
    }

    [Fact]
    public void b2_example_3_vip_110k_60m_50000()
    {
        // VIP 110,000/h, C(n)=round(110000*n/60). C(27)=49,500 paid; balance 500 < marginal C(28)-C(27)=1,833 -> grace 1,833;
        // rest 32 min = C(60)-C(28) = 110,000-51,333 = 58,667, balance pays 500 -> burnt 58,167; consumed 49,500+500 = 50,000.
        var start = T.At(2026, 6, 3, 14, 0);
        var play = CustomerPlay.Settle(
            PlayPc(start, start.AddMinutes(60), PilotSeed.PcVipGroupId),
            Wallet(50_000));
        AssertBurnt(play, 1_833, 58_167, 50_000);
    }

    [Fact]
    public void b2_example_4_vip_night_130k_240s_5000()
    {
        // Night VIP 130k, 4 billed minutes marginals 2167+2166+2167+2167=8667; freeze grace 2167, burnt 1500, collected 5000.
        var start = T.At(2026, 6, 3, 2, 0);
        var play = CustomerPlay.Settle(
            PlayPc(start, start.AddSeconds(240), PilotSeed.PcVipGroupId),
            Wallet(5_000));
        AssertBurnt(play, 2_167, 1_500, 5_000);
    }

    [Fact]
    public void b2_example_5_band_boundary_unchanged_1167_burnt()
    {
        var start = T.At(2026, 6, 2, 23, 58);
        var end = T.At(2026, 6, 3, 0, 1);
        var play = CustomerPlay.Settle(PlayPc(start, end), Wallet(1_833));
        AssertBurnt(play, 1_334, 1_167, 1_833);
    }

    [Fact]
    public void b2_example_6_eighty_thousand_120s_stops_after_grace_no_burnt()
    {
        // 120s → 2 minutes; grace ends play on minute 2; wallet 500 retained, collected 1333.
        var start = T.At(2026, 6, 3, 14, 0);
        var play = CustomerPlay.Settle(PlayPc(start, start.AddSeconds(120)), Wallet(1_833));
        Assert.Equal(1_334, play.Settlement.Grace!.WaivedToman);
        Assert.Equal(0, play.Settlement.WriteOffToman);
        Assert.Equal(1_333, play.Settlement.MoneyConsumedToman);
        Assert.Equal(500, play.Account.PcMoneyToman);
        Assert.Null(play.Settlement.Alert);
    }

    [Fact]
    public void b2_example_7_balance_covers_post_grace_no_write_off_no_alert()
    {
        // Wallet 2666 covers m1 1333 + m3 1333; grace m2 full 1334; burnt 0 (B2-Q1).
        var start = T.At(2026, 6, 3, 14, 0);
        var play = CustomerPlay.Settle(PlayPc(start, start.AddSeconds(180)), Wallet(2_666));
        AssertBurnt(play, 1_334, 0, 2_666);
    }

    [Fact]
    public void b2_titan_28_post_grace_burnt_oracle_from_discounted_marginals()
    {
        // gross marginals 1333, 1334, 1333; 28% discount is applied to the running (cumulative) line total:
        // net totals 960 / 1921 / 2880, so net marginals 960 / 961 / 959. wallet 1460 (=960+500).
        // grace 961; m3 pay 500; burnt 959-500=459; consumed 1460.
        var start = T.At(2026, 6, 3, 14, 0);
        var play = CustomerPlay.Settle(
            PlayPc(start, start.AddSeconds(180)),
            DiscountWallet(1_460),
            pricing: new CustomerPricing("Titan", 28, null, 0));
        Assert.Equal(961, play.Settlement.Grace!.WaivedToman);
        Assert.Equal(459, play.Settlement.WriteOffToman);
        Assert.Equal(1_460, play.Settlement.MoneyConsumedToman);
        Assert.Equal(1_460, play.ChargeInvoice!.PayableToman);
        Assert.Equal(459, play.Settlement.Alert!.AmountToman);
        Assert.Equal(0, play.Account.PcMoneyToman);
    }
}
