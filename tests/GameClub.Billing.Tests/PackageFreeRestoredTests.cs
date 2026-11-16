using System.Diagnostics;
using GameClub.Billing;

namespace GameClub.Billing.Tests;

/// <summary>Non-package checks restored from deleted Round7/8/9R8 tests (category A).</summary>
public sealed class PackageFreeRestoredTests
{
  [Fact]
  public void one_year_settle_stays_under_budget()
  {
    var flat = new RateCardBuilder().AddBase("g", PlayMode.Offline, 1, 110_000).Build();
    var policy = BillingPolicy.Default;
    var start = T.At(2025, 6, 2, 10, 0);
    var end = start.AddYears(1);
    var charge = SessionPricer.Price(
      new Session("y", [new PlaySegment("a", start, end, "st", "g", PlayMode.Offline, 1)]),
      flat,
      policy: policy);
    var sw = Stopwatch.StartNew();
    _ = AccountSettlement.Settle(charge, new PcAccount("m", 10_000_000_000), billing: policy);
    sw.Stop();
    var budgetMs = Math.Max(100, sw.ElapsedMilliseconds * 10);
    Assert.True(sw.ElapsedMilliseconds < budgetMs, $"one-year settle took {sw.ElapsedMilliseconds} ms (budget {budgetMs} ms)");
  }

  [Fact]
  public void station_directory_lookup_ignores_case_and_surrounding_spaces()
  {
    var stations = new StationDirectory(
      [new StationGroup("g", "PC", StationKind.Pc)],
      [new Station("pc-1", "PC 1", "g")]);
    var start = T.At(2026, 6, 2, 10, 0);
    var session = new Session("ok", [
      new PlaySegment("a", start, start.AddMinutes(10), " pc-1 ", "g", PlayMode.Offline, 1),
    ]);
    var charge = SessionPricer.Price(session, Flat(), stations: stations);
    Assert.True(charge.TotalBilledMinutes > 0);
  }

  [Fact]
  public void station_directory_rejects_duplicate_after_normalizing_whitespace()
  {
    var group = new StationGroup("pcg", "PC", StationKind.Pc);
    var ex = Assert.Throws<BillingException>(() =>
      new StationDirectory(
        [group],
        [new Station("pc1", "a", "pcg"), new Station(" pc1 ", "b", "pcg")]));
    Assert.Equal(BillingErrors.InvalidTariff, ex.Code);

    var dir = new StationDirectory([group], [new Station(" pc9 ", "padded", "pcg")]);
    Assert.True(dir.TryGetGroup("pc9", out _));
    Assert.True(dir.TryGetGroup(" pc9 ", out _));
  }

  [Fact]
  public void r8_3_station_lookup_is_case_insensitive_and_rejects_internal_space_aliases()
  {
    var group = new StationGroup("pcg", "PC", StationKind.Pc);
    var dir = new StationDirectory([group], [new Station("PC1", "one", "pcg")]);
    Assert.True(dir.TryGetGroup("pc1", out _));
    Assert.True(dir.TryGetGroup("  Pc1 ", out _));

    var exDup = Assert.Throws<BillingException>(() =>
      new StationDirectory([group], [new Station("pc1", "a", "pcg"), new Station("PC1", "b", "pcg")]));
    Assert.Equal(BillingErrors.InvalidTariff, exDup.Code);

    var start = T.At(2026, 6, 2, 10, 0);
    var badSpace = new Session("bad", [
      new PlaySegment("a", start, start.AddMinutes(5), "pc 1", "pcg", PlayMode.Offline, 1),
    ]);
    Assert.Throws<BillingException>(() => SessionPricer.Price(badSpace, Flat(), stations: dir));
  }

  [Fact]
  public void r8_3_station_registration_normalizes_arabic_letters_and_digits()
  {
    var group = new StationGroup("pcg", "PC", StationKind.Pc);
    var dir = new StationDirectory(
      [group],
      [new Station(" \u06431 ", "kaf", "pcg")]);
    Assert.True(dir.TryGetGroup("\u06A91", out _));
  }

  [Fact]
  public void segment_group_must_match_station_directory()
  {
    var stations = new StationDirectory(
      [new StationGroup("vip", "VIP", StationKind.Pc), new StationGroup("norm", "Normal", StationKind.Pc)],
      [new Station("pc-1", "PC", "vip")]);
    var start = T.At(2026, 6, 2, 10, 0);
    var session = new Session("x", [
      new PlaySegment("a", start, start.AddMinutes(10), "pc-1", "norm", PlayMode.Offline, 1),
    ]);
    var ex = Assert.Throws<BillingException>(() => SessionPricer.Price(session, Flat(), stations: stations));
    Assert.Equal(BillingErrors.InvalidSession, ex.Code);
  }

  private static RateCard Flat()
    => new RateCardBuilder().AddBase("g", PlayMode.Offline, 1, 110_000).Build();
}