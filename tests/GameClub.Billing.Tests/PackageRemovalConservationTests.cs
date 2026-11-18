using GameClub.Billing;

namespace GameClub.Billing.Tests;

/// <summary>Money-only conservation fuzz from deleted Round9R8 corpus tests (B5').</summary>
public sealed class PackageRemovalConservationTests
{
  static BillingPolicy G5()
    => new(5, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);

  static RateCard Flat()
    => new RateCardBuilder().AddBase("g", PlayMode.Offline, 1, 110_000).Build();

  static long Parts(Settlement st)
    => st.MoneyConsumedToman + st.DiscountToman
      + (st.Grace?.WaivedToman ?? 0) + st.WriteOffToman + st.UncoveredToman + st.OmittedToman;

  [Fact]
  public void r8_1_conservation_corpus_without_settlement_plug()
  {
    var rng = new Random(707);
    var flat = Flat();
    var birth = JalaliDate.FromGregorian(new LocalDate(2026, 6, 6));
    var granularities = new[] { 1, 5, 15 };
    for (var i = 0; i < 2_000; i++)
    {
      var g = granularities[rng.Next(granularities.Length)];
      var policy = new BillingPolicy(g, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
      var start = TehranTime.AddSeconds(T.At(2026, 6, 5, 20, 0), rng.Next(0, 8 * 3600));
      var end = TehranTime.AddSeconds(start, rng.Next(30, 2 * 3600));
      var charge = SessionPricer.Price(
        new Session("x", [new PlaySegment("a", start, end, "st", "g", PlayMode.Offline, 1)]),
        flat,
        policy: policy);
      var balance = rng.Next(0, 300_000);
      var spec = new SessionDiscount(
        LevelPercent: rng.Next(3) == 0 ? 0 : 28,
        BirthdayPercent: rng.Next(5) == 0 ? 20 : 0,
        CodePercent: rng.Next(3) == 0 ? 0 : rng.Next(1, 30),
        FixedToman: rng.Next(4) == 0 ? rng.Next(1, 20_000) : 0,
        BirthDate: rng.Next(5) == 0 ? birth : null,
        ValidFrom: rng.Next(2) == 0 ? TehranTime.AddSeconds(start, rng.Next(0, 3600)) : null);
      var st = AccountSettlement.Settle(
        charge,
        new PcAccount("m", balance),
        billing: policy,
        discount: spec);
      Assert.Equal(charge.TotalAmountToman, Parts(st));
      Assert.True(st.MoneyConsumedToman >= 0);
      Assert.True(st.WriteOffToman >= 0);
      Assert.True(st.DiscountToman >= 0);
    }
  }

  [Fact]
  public void r8_1_money_first_conservation_corpus_payable_equals_debit()
  {
    var rng = new Random(708);
    var flat = Flat();
    var pilot = PilotSeed.CreateCard();
    var cards = new (RateCard Card, string GroupId)[]
    {
      (pilot, PilotSeed.PcVipGroupId),
      (pilot, PilotSeed.PcNormalGroupId),
      (flat, "g"),
    };
    var titan = new CustomerPricing("Titan", 28, null, 0);
    var birth = JalaliDate.FromGregorian(new LocalDate(2026, 6, 6));
    var granularities = new[] { 1, 5, 15 };
    for (var i = 0; i < 2_000; i++)
    {
      var (card, groupId) = cards[rng.Next(cards.Length)];
      var g = granularities[rng.Next(granularities.Length)];
      var policy = new BillingPolicy(g, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
      var start = TehranTime.AddSeconds(T.At(2026, 6, 5, 20, 0), rng.Next(0, 8 * 3600));
      var end = TehranTime.AddSeconds(start, rng.Next(30, 2 * 3600));
      var charge = SessionPricer.Price(
        new Session("x", [new PlaySegment("a", start, end, "st", groupId, groupId is PilotSeed.PcNormalGroupId or PilotSeed.PcVipGroupId ? PlayMode.Online : PlayMode.Offline, 1)]),
        card,
        policy: policy);
      var balance = rng.Next(0, 300_000);
      CustomerPricing? pricing = rng.Next(3) == 0 ? null : titan;
      DiscountCode? code = null;
      if (rng.Next(3) != 0)
      {
        code = new DiscountCode(
          "F",
          rng.Next(0, 30),
          rng.Next(4) == 0 ? rng.Next(1, 20_000) : 0,
          rng.Next(2) == 0 ? TehranTime.AddSeconds(start, rng.Next(0, 3600)) : null,
          null,
          null,
          null);
      }

      var account = new CustomerAccount(
        "m",
        WalletSettings.Shared with { ApplyDiscountsToPc = pricing is not null || code is not null },
        new WalletPocket(balance, 0),
        new WalletPocket(0, 0));
      var result = CustomerPlay.Settle(
        charge,
        account,
        billing: policy,
        pricing: pricing,
        code: code,
        issuedAt: charge.Lines[charge.Lines.Count - 1].End);
      var st = result.Settlement;
      Assert.Equal(charge.TotalAmountToman, Parts(st));
      Assert.True(st.MoneyConsumedToman >= 0);
      Assert.True(st.WriteOffToman >= 0);
      Assert.True(st.DiscountToman >= 0);
      Assert.True(result.Account.PcMoneyToman >= 0);
      Assert.True(result.Account.PcMoneyToman <= balance);
      if (result.ChargeInvoice is not null)
      {
        Assert.Equal(st.MoneyConsumedToman, result.ChargeInvoice.PayableToman);
      }
    }
  }
}