using GameClub.Billing;

namespace GameClub.Billing.Tests;

/// <summary>B5' money-only replacements for deleted package-scenario billing tests.</summary>
public sealed class PackageRemovalDeletedTestReplacements
{
  const string V = PilotSeed.PcVipGroupId;

  static BillingPolicy G(int granularity)
    => new(granularity, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);

  static RateCard Flat()
    => new RateCardBuilder().AddBase("g", PlayMode.Offline, 1, 110_000).Build();

  static long Parts(Settlement st)
    => st.MoneyConsumedToman + st.DiscountToman
      + (st.Grace?.WaivedToman ?? 0) + st.WriteOffToman + st.UncoveredToman + st.OmittedToman;

  static (Settlement st, SessionCharge charge) MoneyFirstVipBand(long balance, SessionDiscount? discount = null)
  {
    var start = T.At(2026, 6, 2, 23, 50);
    var end = T.At(2026, 6, 3, 0, 20);
    var charge = SessionPricer.Price(
      new Session("x", [new PlaySegment("a", start, end, "st", V, PlayMode.Offline, 1, StationKind.Pc)]),
      PilotSeed.CreateCard(),
      policy: G(1));
    var settled = AccountSettlement.Settle(charge, new PcAccount("m", balance), G(1), discount: discount);
    return (settled, charge);
  }

  [Fact]
  public void rc_197_money_first_keeps_fixed_code_discount()
  {
    var policy = G(5);
    var start = T.At(2026, 6, 2, 22, 0);
    var charge = SessionPricer.Price(
      new Session("x", [new PlaySegment("a", start, start.AddMinutes(63), "st", "g", PlayMode.Offline, 1)]),
      Flat(),
      policy: policy);
    var code = DiscountCode.FixedOff("FX", 5_000, validFrom: T.At(2026, 6, 2, 23, 1));
    var result = CustomerPlay.Settle(
      charge,
      new CustomerAccount(
        "m",
        WalletSettings.Shared with { ApplyDiscountsToPc = true },
        new WalletPocket(70_000, 0),
        new WalletPocket(0, 0)),
      billing: policy,
      pricing: new CustomerPricing("Titan", 28, null, 0),
      code: code,
      issuedAt: start.AddMinutes(63));
    var st = result.Settlement;
    Assert.Equal(charge.TotalAmountToman, Parts(st));
    Assert.Equal(38_366, st.DiscountToman);
    Assert.Equal(70_000, st.MoneyConsumedToman);
  }

  [Fact]
  public void rc_202_money_first_band_edge_birthday_uses_discounted_marginals()
  {
    var birthday = new SessionDiscount(
      BirthdayPercent: 20,
      BirthDate: JalaliDate.FromGregorian(new LocalDate(2026, 6, 3)));
    var (st, _) = MoneyFirstVipBand(27_000, birthday);
    Assert.Equal(27_000, st.MoneyConsumedToman);
    Assert.Null(st.Grace);
  }

  [Fact]
  public void rc_202_money_first_band_edge_code_from_midnight_uses_discounted_marginals()
  {
    var code = new SessionDiscount(CodePercent: 50, ValidFrom: T.At(2026, 6, 3, 0, 0));
    var (st, _) = MoneyFirstVipBand(23_750, code);
    Assert.Equal(23_750, st.MoneyConsumedToman);
    Assert.Null(st.Grace);
  }

  [Fact]
  public void block_billing_net_of_keeps_negative_step_when_fixed_exceeds_minute_net()
  {
    var start = T.At(2026, 6, 2, 22, 0);
    var charge = SessionPricer.Price(
      new Session("x", [new PlaySegment("a", start, start.AddMinutes(51), "st", "g", PlayMode.Offline, 1)]),
      Flat(),
      policy: G(5));
    var spec = new SessionDiscount(LevelPercent: 28, FixedToman: 5_000, ValidFrom: T.At(2026, 6, 2, 22, 48));
    var settled = AccountSettlement.Settle(charge, new PcAccount("m", 51_816), billing: G(5), discount: spec);
    Assert.Equal(charge.TotalAmountToman, Parts(settled));
    Assert.Equal(51_816, settled.MoneyConsumedToman);
    Assert.Equal(0, settled.OmittedToman);
    Assert.Equal(14_465, settled.WriteOffToman);
  }

  [Fact]
  public void negative_grace_minute_conserves_gross_and_settle_does_not_throw()
  {
    var flat = Flat();
    var policy = G(5);
    var start = T.At(2026, 6, 2, 22, 0);
    var charge = SessionPricer.Price(
      new Session("x", [new PlaySegment("a", start, start.AddMinutes(63), "st", "g", PlayMode.Offline, 1)]),
      flat,
      policy: policy);
    var code = new DiscountCode("FX", 0, 5_000, T.At(2026, 6, 2, 23, 1), null, null, null);
    var result = CustomerPlay.Settle(
      charge,
      new CustomerAccount(
        "m",
        WalletSettings.Shared with { ApplyDiscountsToPc = true, GraceMinuteEnabled = true },
        new WalletPocket(1_000_000, 0),
        new WalletPocket(0, 0)),
      billing: policy,
      pricing: new CustomerPricing("Titan", 28, null, 0),
      code: code,
      issuedAt: start.AddMinutes(63));
    var st = result.Settlement;
    var sum = st.MoneyConsumedToman + st.DiscountToman
      + (st.Grace?.WaivedToman ?? 0) + st.WriteOffToman + st.UncoveredToman + st.OmittedToman;
    Assert.Equal(charge.TotalAmountToman, sum);
    Assert.NotNull(result.ChargeInvoice);
    Assert.Equal(st.MoneyConsumedToman, result.ChargeInvoice!.PayableToman);
  }

  [Fact]
  public void money_then_packages_uses_each_minutes_half_up_price_for_code_gross()
  {
    var flat = new RateCardBuilder().AddBase("g", PlayMode.Offline, 1, 110_000).Build();
    var policy = G(5);
    var start = T.At(2026, 6, 4, 12, 0);
    var end = start.AddHours(2);
    var charge = SessionPricer.Price(
      new Session("s", [new PlaySegment("a", start, end, "st", "g", PlayMode.Offline, 1)]),
      flat,
      policy: policy);
    var spec = new SessionDiscount(
      LevelPercent: 28,
      CodePercent: 15,
      FixedToman: 5_000,
      ValidFrom: start.AddMinutes(30));
    var settled = AccountSettlement.Settle(charge, new PcAccount("m", 500_000), billing: policy, discount: spec);
    var expected = Round6Tests.IndependentOracleDiscountForCharge(charge, spec);
    Assert.Equal(expected, settled.DiscountToman);
  }

  [Theory]
  [InlineData(5, 8)]
  [InlineData(5, 10)]
  [InlineData(15, 8)]
  [InlineData(15, 10)]
  public void large_balance_after_package_minutes_has_no_phantom_grace(int granularity, int _)
  {
    var flat = Flat();
    var policy = new BillingPolicy(granularity, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
    var start = T.At(2026, 6, 2, 22, 0);
    var charge = SessionPricer.Price(
      new Session("x", [new PlaySegment("a", start, start.AddMinutes(63), "st", "g", PlayMode.Offline, 1)]),
      flat,
      policy: policy);
    var settled = AccountSettlement.Settle(charge, new PcAccount("m", 1_000_000), billing: policy);
    Assert.Null(settled.Grace);
    Assert.Equal(0, settled.OmittedToman);
    Assert.Equal(charge.TotalAmountToman, Parts(settled));
  }

  [Fact]
  public void r8_1_minimal_repro_packages_then_money_pins_debit_discount_and_parts()
  {
    var policy = G(5);
    var start = T.At(2026, 6, 2, 22, 0);
    var charge = SessionPricer.Price(
      new Session("x", [new PlaySegment("a", start, start.AddMinutes(63), "st", "g", PlayMode.Offline, 1)]),
      Flat(),
      policy: policy);
    var result = CustomerPlay.Settle(
      charge,
      new CustomerAccount(
        "m",
        WalletSettings.Shared with { ApplyDiscountsToPc = true },
        new WalletPocket(1_000_000, 0),
        new WalletPocket(0, 0)),
      billing: policy,
      pricing: new CustomerPricing("Titan", 28, null, 0),
      code: new DiscountCode("FX", 0, 5_000, T.At(2026, 6, 2, 23, 1), null, null, null),
      issuedAt: start.AddMinutes(63));
    var st = result.Settlement;
    Assert.Equal(charge.TotalAmountToman, Parts(st));
    Assert.Equal(80_801, st.MoneyConsumedToman);
    Assert.Equal(38_366, st.DiscountToman);
    Assert.Equal(0, st.WriteOffToman);
    Assert.Null(st.Grace);
    Assert.Null(st.Alert);
  }
}