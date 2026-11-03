# Game Club Manager — billing engine

Pure pricing and session calculation for a PC and console gaming cafe (گیم‌نت) in Iran. This is the first technical spike: a dependency-free class library and xUnit tests. There is no UI, database, or web API.

The repository had no application code, only a title, so this is a new solution rather than an extension of an existing layout.

```text
GameClub.slnx
src/GameClub.Billing/          net10.0 class library
tests/GameClub.Billing.Tests/  xUnit
```

```bash
dotnet test
```

Money is a `long` in toman. Nothing in the library reads the clock, and nothing uses `double` or `decimal` for a charge.

## خلاصه فارسی

این کتابخانه فقط محاسبهٔ قیمت و زمان است. مبلغ‌ها تومان و عدد صحیح‌اند. ساعت را خودِ برنامه می‌دهد (از ساعت سیستم خوانده نمی‌شود) و همیشه به وقت تهران با اختلاف `+۰۳:۳۰` تفسیر می‌شود. ایران از ۲۰۲۲ ساعت تابستانی ندارد و این موتور هم تعویض ساعت را اعمال نمی‌کند.

نرخ یک لحظه به این ترتیب انتخاب می‌شود: **روز خاصِ همان گروه > روز هفته > بازهٔ ساعتیِ همان گروه > نرخ پایه**. هر گروه بازهٔ ساعتی خودش را دارد؛ بازهٔ PS5 روی گروه دیگر اثر نمی‌گذارد. اگر برای لایهٔ بالاتر نرخی تعریف نشده باشد، به لایهٔ بعدی برمی‌گردد. سانسِ طولانی، حتی اگر از نیمه‌شب یا از مرز «ساعات خاص» رد شود، خودش تکه‌تکه می‌شود.

تخفیف سطح مشتری و تخفیف تولد با هم جمع می‌شوند (سقف پیش‌فرض ۱۰۰٪) و فقط روی زمان بازی اعمال می‌شوند. کد تخفیف جداست و محدودهٔ آن قابل تنظیم است: زمان، بوفه، خدمات ویژه، یا ترکیبی از آن‌ها. مبلغ نهایی منفی نمی‌شود. هر دقیقهٔ شروع‌شده حساب می‌شود. اگر موجودی پول کافی نباشد، حساب منفی (بدهکار) می‌شود؛ سقف بدهی اختیاری است و پیش‌فرض نامحدود است. کیف پول، اعتبار هدیه و واقعی را مثل یک موجودی و به نسبت مصرف می‌کند و سهم هر کدام را جدا نشان می‌دهد. تعطیلات فهرست ثابت ندارند؛ کافه خودش روزهای خاص و جدول نرخ هر گروه را وارد می‌کند.

فقط نرخ PS5 از Game Timer موجود است. گروه ناشناخته خطا می‌دهد و رایگان حساب نمی‌شود.

## Domain

| Type | Role |
| --- | --- |
| `StationGroup` | User-defined group (`معمولی`, CIP, VIP, VVIP, `ps5`, billiard, …) with a kind: PC, console, or timed table. Unlimited groups. |
| `Station` | Belongs to exactly one group. `StationDirectory` is the lookup. |
| `PlayMode` | `Offline` is priced by player/controller count 1–4. `Online` and `Vr` are a single rate; the count is recorded but does not select the tariff. |
| `TimeBand` | Half-open local interval `[start, end)` **for one station group**. `end <= start` wraps midnight. Bands of the same group must not overlap. Another group's bands are ignored. Gaps use that group's base tariff. |
| `RateCard` | Base rates, per-group band rates, per-weekday rates, and special-day rate tables keyed by group. |
| `SpecialDay` / `SpecialDayCalendar` | Cafe-defined dates. No built-in holiday list. Each date points at a rate table; the same table can price groups differently, and several dates can share a table. |
| `PlaySegment` | One stretch at one station, group, mode, and player count. |
| `Session` | Ordered segments plus pause intervals. |
| `TimePackage` / `PcAccount` | Remaining package minutes and a money balance. |
| `Invoice` | Time lines, buffet, fixed services, discounts, rounding, payable total. |
| `SplitPayment` | Cash, card reader, card-to-card, and member wallets. |

A segment is what the caller already knows: the controller count changed, the member moved to another station, or the mode changed. The engine does not infer those events. It does infer rate changes.

## How a session is priced

`SessionPricer.Price` takes the session, a rate card, a holiday calendar, and a `BillingPolicy`.

1. Reject overlapping segments, a reversed interval, a player count outside 1–4 for offline play, or an instant whose offset is not `+03:30`.
2. Subtract pauses. A pause cuts every segment it overlaps. Time outside the session is ignored. Overlapping pauses are safe.
3. Split each active stretch at Tehran midnight and at the edges of **that segment's group** bands. A band that belongs to another group is not a boundary.
4. Resolve the rate at the start of each piece (see precedence).
5. Merge pieces of the **same caller segment** when they are contiguous, on the same Tehran date, and resolved to the same rate. A band edge that does not change the price is not an extra rounded minute. Pieces the caller already split stay separate, even at the same rate.
6. Turn each merged piece's active seconds into billed minutes, then into toman.

The charge for a piece is integer division:

```text
numerator = hourlyRate × billedMinutes
charge    = round(numerator / 60)
```

`round` is floor, ceiling, or half-up on `numerator % 60`. Half-up (the default) rounds a remainder of 30 or more up, so a single PS5 minute at 110,000 is **1,833 toman** (remainder 20), not the floating `1,833.333…` stored in Game Timer.

Logout is the segment's end. Nothing after that instant is billed, and whatever is left on the account stays.

Sessions may cross midnight, cross several days, and run longer than 24 hours. A 30-hour PS5 1-player example (Tuesday 22:00 → Thursday 04:00, special hours 00:00–08:30) bills **3,925,000 toman** in four lines: 2 h base, 8.5 h special, 15.5 h base, 4 h special.

## Configuration

`BillingPolicy` (default in parentheses):

| Field | Meaning |
| --- | --- |
| `GranularityMinutes` (1) | Billing block size. 15 means time is billed in 15-minute blocks. |
| `PartialUnits` (Ceiling) | What happens to an incomplete block. Ceiling bills it as a full block. Floor drops it. Nearest rounds an exact half up, so 30 seconds of a 1-minute block is billed and 29 seconds are not. |
| `Remainder` (Nearest) | The toman remainder of dividing by 60, as above. |

`AccountPolicy` spends package minutes before money. `DebtPolicy` defaults to **unlimited**: a short money balance becomes a negative debt. `DebtPolicy.None` stops at zero. `DebtPolicy.Limited(amount)` refuses to go below minus that amount, and the rest is `UncoveredToman`. The resulting balance, including a negative one, is on the updated account (`DebtToman` is the positive size of that debt).

`DiscountPolicy` adds the level percent and the birthday percent, caps that sum at `MaxCombinedPercent` (default **100**), and applies it to time charges only. `Birthday` defaults to the exact day. `BirthdayWindow.DaysOnAndAfter(7)` is the birthday plus the next six days. `BirthdayWindow.DaysAround(7)` is three days before, the day, and three days after. An even span puts the extra day after the birthday. `BirthdayWindow.WholeMonth` is the birth month. February 29 is observed on February 28 in a non-leap year.

A discount code's `ChargeScope` defaults to time. Combine `Time`, `Buffet`, and `Services` when the code should cover more than one part.

`RoundingPolicy` is **off** by default. A branch turns it on with a step, a direction, and a scope.

Wallet draws do not order gift before real or the reverse. They take both as one balance.

## Rate precedence

For one instant, one group, and one tariff key `(group, mode, player count)`:

1. **Special day**, if the cafe put that Tehran date on the calendar and the date's rate table has a rate for this group and key.
2. **Weekday**, if that day has a rate for the key.
3. **This group's time band**, if local time falls in one of its bands and that band has a rate for the key.
4. **Base tariff**.

A missing rate falls through. A special day does not need a copy of every mode, and it does not need a rate for every group: a group with no row on that table keeps its weekday, band, or base rate. PS5's 00:00–08:30 band does not split or reprice any other group. If nothing matches, pricing throws `BillingException` with code `tariff_not_found` rather than charging zero. Only PS5 rates are live; other groups in tests use rates marked as examples.

Worked PS5 1-player figures from the owner's sheet:

| Window | Rate |
| --- | --- |
| 23:00–00:00 | 110,000 / hour → 110,000 |
| 00:00–01:30 | 160,000 / hour → 240,000 |
| **23:00–01:30, one segment** | **350,000**, two lines, no manual cut |
| 08:00–08:30 | 80,000 |
| 08:30–09:00 | 55,000 |

The band is half-open: 08:30 is already the day rate.

Weekday and special-day rates replace the band for the keys they define. They are not multiplied by it. On a Friday whose 1-player rate is 100,000, 01:00–02:00 costs 100,000 even though special hours would have been 160,000. A 2-player rate that was not overridden still uses the band. A started minute is billed (partial blocks round up). That default is what the cafe confirmed; floor and nearest remain available on `BillingPolicy` if a branch ever needs them.

## Prepaid limits

`PrepaidPlanner.ForDuration` adds the play duration to the start and prices that continuous stretch, including band splits. Pauses are not predicted; a later pause extends the wall clock, not the budget. Re-price the real session when it ends.

`PrepaidPlanner.ForAmount` walks forward across the same boundaries and grants the longest prefix whose charge, under the active minute and toman rules, does not exceed the remaining budget. It will not sell a partial block that rounds up into a minute the balance cannot cover. A remainder smaller than one minute is unused and stays with the caller.

Example, PS5 1 player, budget 160,000, start 08:00:

- 08:00–08:30 at 160,000 costs 80,000.
- The remaining 80,000 buys **43 minutes** at 110,000 (78,833 toman). The 44th minute would be 80,667.
- End is **09:13**. Unused **1,167**. Consumed 158,833, which is under the budget.

The walk stops after 366 days if a zero rate never exhausts the budget.

## PC accounts

A time package stores **remaining minutes**. The face value (`2 hours = 200,000 toman`) is recorded and is not used in consumption. One package minute covers one billed minute in an allowed group, at VIP or at the ordinary rate.

Optional limits: allowed groups, an hour window `[from, until)` that may wrap midnight, and `ValidThrough` (inclusive Tehran date). Among eligible packages, the soonest expiry is spent first, then list order.

Default consumption is package minutes, then money. `MoneyThenPackages` is the other way. Money never goes below zero. A shortfall is `UncoveredToman` for the counter; it is not a negative balance. The gross, the toman value covered by packages, the money drawn, and the shortfall add up to the session charge.

If a priced line straddles a package window, settlement splits that line at the window and allocates the original toman and billed minutes in proportion to the seconds (the tail gets the rounding remainder). The session total does not change. A package with no window, or a line that already ended on the boundary, is all-or-nothing.

`RemainingTimeEstimator.Estimate` answers "if they start **now** on this group". Eligible package minutes are counted 1:1. The money balance is converted with the rate in force at that instant only — the next band is not projected, which is why the same balance is a different number of minutes on an ordinary example group versus an example VIP group. A zero or negative balance contributes no money minutes. A zero hourly rate is free: money minutes are null, because the balance is not what is buying the time.

Money still due after packages (or after the positive balance, when money is configured first) is posted to the account. By default the balance may go negative and that debt is visible. A later visit on the same account adds to the debt. A limited policy stops at the floor and returns the remainder as uncovered, for the counter.

The account tests use an example ordinary rate of 110,000 and an example VIP rate of 220,000. Those are not live tariffs. With a 220,000 toman balance the ordinary estimate is 120 minutes and the example VIP estimate is 60, before any package minutes.

## Invoice, discounts, rounding

`InvoiceCalculator.Calculate` runs in this order:

1. Time lines, buffet lines (`quantity × unit price`), fixed services (damage, extra person, …).
2. Discounts.
3. Rounding.
4. The payable total, which is what split payment must match.

Line amounts sum to the payable total. Discount lines are negative. A rounding line is omitted when the adjustment is zero. A zero net is not lifted to the rounding step.

### Discount stacking

Level and birthday are separate discounts and they are **added**. A 5% level and a 10% birthday are 15% of the time charge, shown as two lines. The sum of those two percents is capped. The default cap is **100%**: 80% + 40% becomes 100%, not 120%. `DiscountPolicy.MaxCombinedPercent` can be set lower (0–100). It cannot be set above 100.

While the sum is within the cap, each percent is floored on its own base (`amount × percent / 100`), so the lines stay exact toman figures. When the cap binds, the eligible time is charged at the capped percent and that toman is split between the two lines in proportion to the original percents (the birthday line keeps the remainder).

The birthday window is the cafe's, not a flag on the invoice. It is checked against each time line's service date (or `IssuedAt` when a line has no date):

| Window | Meaning |
| --- | --- |
| Exact day (**default**) | The birthday month and day. |
| `DaysOnAndAfter(N)` | The birthday and the next N−1 days. N = 7 is one week starting on the birthday. |
| `DaysAround(N)` | N days with the birthday inside. Odd N is centered. Even N puts the extra day after the birthday. |
| Whole birth month | Any day in the birth month. |

A line outside the window gets the level percent only. February 29 is observed on February 28 when the year is not a leap year.

Customer discounts apply to **time charges only**. Buffet and fixed services are not reduced by level or birthday. A 900,000 toman controller-damage fine stays 900,000.

A code is either a percent or a fixed toman amount, not both, and it is a third line. Its scope is `ChargeScope`: time (the default), buffet, services, or any combination such as time and buffet together. A fixed code cannot exceed that scope's gross. `ValidFrom` is inclusive and `ValidUntil` is exclusive, compared with `IssuedAt`. Outside the window the code is ignored (`outside_window`). A group or weekday restriction filters **time** lines only; buffet and services included in the scope are still discounted in full. If the scope base is zero, the code is `not_eligible`.

If discounts would push a part below zero, the code is reduced first, then the birthday, then the level. The payable total is never negative. A 100% level plus a 50% time code on an 80,000 time charge with a 20,000 buffet item leaves the buffet: payable 20,000. The code does not eat the buffet unless its scope includes buffet.

Suggested level names from the product brief, **not** from the export (the sheet had the columns and no data rows): Bronze 5%, Silver 8%, Gold 12%, Platinum 16%, Diamond 20%, Titan 28%. Pass the percent in; the engine does not look the name up.

## Rounding (PA-8)

Applied after discounts, before split payment. Disabled until a branch enables it.

| Setting | Values |
| --- | --- |
| Step | 1,000, 5,000, 10,000, or any positive custom step |
| Direction | Up (**default when enabled**), down, nearest. An exact half rounds up. |
| Scope | `WholeInvoice`, or `TimeChargesOnly` (the net time charge is rounded, then buffet and services are added) |

138,700 with step 5,000:

| Direction | Payable | Rounding line |
| --- | --- | --- |
| Up | 140,000 | +1,300 |
| Down | 135,000 | −3,700 |
| Nearest | 140,000 | +1,300 |
| Disabled | 138,700 | none |

Time 98,700 plus a 3,000 service, step 5,000 up: the whole invoice becomes 105,000 (+3,300); time-only becomes 103,000 (+1,300 on the time charge, service left as 3,000).

A fixed code of 61,300 on a 200,000 time charge produces 138,700 and only then rounds, so rounding cannot undo a discount.

## Split payment and wallets

`PaymentSplitter.Split` accepts cash, card reader, card-to-card, and wallet tenders. It checks:

- no negative tender
- wallet tenders name a known member
- the requested sum equals the payable total (`sum_mismatch`, difference = tendered − payable)
- each wallet tender fits in that member's gift + real balance, in tender order, so two draws on one wallet see the running balance

A failed wallet draw does not partially consume it. Method totals and per-tender gift/real splits are both returned.

Gift and real credit are **one balance**. There is no gift-first or real-first order, and gift credit does not expire. The ledger still reports both parts. The gift toman is `tender × gift / (gift + real)`, floored; the rest of that tender is real, so the two parts add up to the tender. If either side is already zero, the whole tender comes from the other side. A 30,000 tender on a wallet with 10,000 gift and 50,000 real draws 5,000 gift and 25,000 real. A wallet tender cannot go negative. Debt is only the PC account balance.

## What was taken from Game Timer 4.3

Sheet `nerkh`, PS5 rows. The special-hour clock (`00:00`–`08:30`, active) is stored on the 1-player row; the special hourly prices are filled for every PS5 mode except VR, and this card applies that window to those modes.

| Mode | Day rate | 00:00–08:30 |
| --- | --- | --- |
| 1 player | 110,000 | 160,000 |
| 2 players | 125,000 | 175,000 |
| 3 players | 160,000 | 210,000 |
| 4 players | 220,000 | 270,000 |
| Online | 170,000 | 220,000 |
| VR | *blank / 0* | *blank / 0* |

Two exported visits reproduce exactly, because they divide evenly and sit outside special hours:

- ps5 (3), 2 players, 17:46–21:22 (3 h 36 m) → **450,000**
- ps5 (6), 3 players, 19:10–20:55 (1 h 45 m) → **280,000**

Also read, and not a tariff: service `ABC_1` «خسارت انداختن دسته» at 900,000 toman. Buffet examples on that day included «دبی چاکلت» (the line total was 196,000) and «شکلات» at 98,000. They are not hard-coded.

**Not live prices:** PC, Xbox, VIP, ping-pong, foosball, billiard, hockey, TV, and VR were blank. A lookup throws `tariff_not_found`. Tests that need a second group use ids such as `example-vip` and `example-normal`, with rates that exist only in the test fixture.

**Contrast, not a default.** Game Timer computes `minutes × (hourly / 60)` in floating point and then `ROUND(amount, -2)`, nearest 100 toman. One PS5 minute is about 1,833.33 and rounds to **1,800** there. This engine charges **1,833** and does not apply a 100-toman step. Branch rounding starts at the PA-8 steps (1,000 and up) and is off until enabled. A one-minute visit is the case where the two systems disagree; the two sample visits above do not show it.

## Decisions

- Tehran is a fixed offset. Historical daylight-saving dates before 2022 are out of scope; passing one still uses `+03:30`.
- Sub-second ticks are truncated. A started minute is billed. That is the confirmed default.
- Unknown tariffs fail loudly. Only PS5 rates are loaded from Game Timer. Example groups in tests are labeled `example-*` and are not live prices.
- Time bands belong to one station group. Special days are dates the cafe adds, each pointing at a rate table that can differ by group. They replace the band for the keys they define; they are not a multiplier on it.
- Same-rate caller splits may differ by at most the ceiling cost of **one** granularity block (at the PS5 1-player rate, 1,834 toman). Whole-minute splits of a single rate match exactly.
- Package minutes are minutes, not "toman of face value".
- The remaining-time estimate does not look ahead to the next band. The prepaid amount planner does. A negative balance contributes zero minutes to the estimate.
- Level and birthday percents are summed and capped at 100% by default, on time only. Codes are separate and choose their own scope. Codes are judged at `IssuedAt`. The birthday window uses each time line's service date.
- Debt defaults to unlimited. The PC-account balance may go negative, and that figure is returned on the account. A cap is optional. Wallet tenders stay funded; they are not the debt balance.
- Wallet gift and real credit move in proportion. The floored gift share can be one toman short of an even split; that toman is reported as real. Gift credit does not expire.
- Customer level names are input. The Bronze…Titan ladder on `GameTimerReference.SuggestedLevels` is still only a suggestion.
- Rounding a tiny amount up to a large step is allowed when rounding is on (100 with step 5,000 becomes 5,000). Zero stays zero.
- Malformed input throws `BillingException`. An unbalanced payment is a result with error codes, not an exception.

## Owner answers already applied

Live prices are PS5 only. Bands are per group. Every started minute is billed. Level and birthday discounts add, with a 100% cap, on time only; codes choose a scope. The birthday window is the exact day, N days on-and-after or around, or the birth month. Wallets spend gift and real as one proportional balance. A short PC-account balance becomes debt, unlimited unless a cap is set. Holidays are whatever dates the cafe enters.

## Still open

1. May a member's wallet tender go into debt as well, or is debt only the PC account (what this build does)?
2. Is observing February 29 on February 28, in a non-leap year, the right birthday rule?
3. When a code includes buffet or services and also has a group restriction, the restriction filters time lines only. Should that code leave buffet and services undiscounted unless the time lines matched?
4. The level ladder's minimum hours were not in the export. The suggested percents are not a confirmed table.
5. A prepaid amount that cannot buy a full minute still grants no time. Say if a grace minute is wanted.
6. Game Timer's nearest-100-toman step is still not offered. Say if a 100 toman rounding step should sit beside 1,000 / 5,000 / 10,000.
