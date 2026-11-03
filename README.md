# Game Club Manager — billing engine

Pure pricing and session calculation for a PC and console gaming cafe (گیم‌نت) in Iran. This is the first technical spike: a dependency-free class library and xUnit tests. There is no UI, database, or web API.

The repository had no application code, only a title, so this is a new solution rather than an extension of an existing layout.

```text
GameClub.slnx
src/GameClub.Billing/          netstandard2.0 and net10.0 class library
tests/GameClub.Billing.Tests/  xUnit
```

```bash
dotnet test
```

Money is a `long` in toman. Nothing in the library reads the clock, and nothing uses `double` or `decimal` for a charge.

## خلاصه فارسی

این کتابخانه فقط محاسبهٔ قیمت و زمان است. مبلغ‌ها تومان و عدد صحیح‌اند. ساعت را خودِ برنامه می‌دهد (از ساعت سیستم خوانده نمی‌شود) و همیشه به وقت تهران با اختلاف `+۰۳:۳۰` تفسیر می‌شود. ایران از ۲۰۲۲ ساعت تابستانی ندارد و این موتور هم تعویض ساعت را اعمال نمی‌کند.

نرخ یک لحظه به این ترتیب انتخاب می‌شود: **روز خاصِ همان گروه > بازهٔ ساعتیِ همان گروه > روز هفته > نرخ پایه**. اگر روز خاص برای آن تعداد بازیکن نرخ نداشته باشد، به لایهٔ بعدی برمی‌گردد و فقط وقتی هیچ نرخی نیست خطا می‌دهد. هر گروه بازهٔ ساعتی خودش را دارد؛ بازهٔ PS5 روی گروه دیگر اثر نمی‌گذارد. اگر برای لایهٔ بالاتر نرخی تعریف نشده باشد، به لایهٔ بعدی برمی‌گردد. سانسِ طولانی، حتی اگر از نیمه‌شب یا از مرز «ساعات خاص» رد شود، خودش تکه‌تکه می‌شود.

تولد، «کل ماه تولد»، پنجرهٔ تخفیف تولد، و روزهای خاص با تقویم شمسی (جلالی) و به وقت تهران حساب می‌شوند. مثلاً کل مهر، یا هر سال ۱ فروردین. تولد ۳۰ اسفند در سال غیرکبیسه روی ۲۹ اسفند دیده می‌شود.

تخفیف سطح و تولد جمع می‌شوند (سقف پیش‌فرض ۱۰۰٪) و فقط روی زمان بازی‌اند. سطح‌ها را خود کافه می‌سازد (نام، حداقل ساعت کل، درصد). کد تخفیف محدودهٔ جدا دارد. اگر کد به یک گروه محدود باشد و بوفه یا خدمات را هم شامل شود، آن بخش فقط وقتی تخفیف می‌گیرد که حداقل یک سطر زمان از همان گروه در فاکتور باشد؛ وگرنه کد اعمال نمی‌شود.

حالت کیف پول تنظیم شعبه است. در حالت مشترک یک موجودی برای PC و کنسول است. بدهی به‌طور پیش‌فرض خاموش است؛ کنسول موجودی را منفی نمی‌کند مگر شعبه سقف بدهی بگذارد. اگر موجودی منفی باشد، PC به‌طور پیش‌فرض بسته می‌شود. اگر `AllowPcWhileInDebt` روشن باشد، PC هنوز می‌تواند دقیقه‌های بسته را مصرف کند ولی پول را منفی‌تر نمی‌کند. در حالت جدا، موجودی PC هرگز منفی نمی‌شود و کیف پول کنسول/میز جداست؛ بدهی کنسول روی ورود PC اثر ندارد. شارژ باید بگوید کدام موجودی را پر می‌کند.

فقط نرخ PS5 از Game Timer موجود است. گروه ناشناخته خطا می‌دهد و رایگان حساب نمی‌شود.

## Domain

| Type | Role |
| --- | --- |
| `StationGroup` | User-defined group (`معمولی`, CIP, VIP, VVIP, `ps5`, billiard, …) with a kind: PC, console, or timed table. Unlimited groups. |
| `Station` | Belongs to exactly one group. `StationDirectory` is the lookup. |
| `PlayMode` | `Offline` is priced by player/controller count 1–4. `Online` and `Vr` are a single rate; the count is recorded but does not select the tariff. |
| `TimeBand` | Half-open local interval `[start, end)` **for one station group**. `end <= start` wraps midnight. Bands of the same group must not overlap. Another group's bands are ignored. Gaps use that group's base tariff. |
| `RateCard` | Base rates, per-group band rates, per-weekday rates, and special-day rate tables keyed by group. |
| `JalaliDate` | Stored Solar Hijri year/month/day. A Gregorian Tehran date is converted with `JalaliDate.FromGregorian` at the edge. |
| `SpecialDay` / `SpecialDayCalendar` | Cafe-defined Jalali rules: one date, or every year on a Jalali month/day (1 Farvardin). A one-off wins over a yearly rule on the same day. No built-in holiday list. |
| `PlaySegment` | One stretch at one station, group, mode, and player count. |
| `Session` | Ordered segments plus pause intervals. |
| `TimePackage` / `PcAccount` | Remaining package minutes and a money balance. |
| `Invoice` | Time lines, buffet, fixed services, discounts, rounding, payable total. |
| `SplitPayment` | Cash, card reader, card-to-card, and member wallets. |

A segment is what the caller already knows: the controller count changed, the member moved to another station, or the mode changed. The engine does not infer those events. It does infer rate changes.

## How a session is priced

`SessionPricer.Price` takes the session, a rate card, a holiday calendar, and a `BillingPolicy`.

1. Reject overlapping segments, a reversed interval, or a player count outside 1–4 for offline play. Any offset is rebuilt from its UTC instant at a fixed `+03:30`. A `DateTimeKind.Local` value is rejected so the machine offset is never used.
2. Subtract pauses. A pause cuts every segment it overlaps. Time outside the session is ignored. Overlapping pauses are safe.
3. Minutes and toman are separate rules. Minutes are the ceiling of played seconds (pauses excluded): 10 minutes 0 seconds is 10 minutes, and 10 minutes 20 seconds is 11. Minutes are never rounded half-up. Half-up applies only to toman, once per line: `hourly × minutes / 60`. Sixty PS5 minutes are exactly 110,000, not 60 × 1,833.
4. Each minute is priced at the rate where that minute starts. A minute that crosses 08:30, midnight, or into a special day stays on the starting rate. Lines merge across a pause. They split only when the rate, the band, or the player count changes, so a paused session at one rate is one line and matches the same active time with no pause. Six hundred active seconds with three pauses is one line of 10 minutes, 18,333 toman. Forty active minutes with one pause is one line of 73,333, not two lines of 36,667.

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

`WalletSettings` is the branch choice. `WalletMode.Shared` (the default) keeps one pocket for PC and for console or tables. `AllowPcWhileInDebt` defaults to false: PC time, including packages, is blocked while that pocket is negative, and the estimate is zero. Set it true and PC may still spend package minutes, but a PC charge never pushes the balance further negative, so money minutes stay zero while the pocket is negative. `WalletMode.Separate` keeps a PC balance that never goes negative and a non-PC wallet that may. Console debt does not affect PC login. `TopUp` names the target: `Shared` in shared mode, or `Pc` / `NonPc` in separate mode. `DebtPolicy` limits only the pocket a console or table charge draws. The default is `None`: the charge is refused rather than going below zero. `Limited(amount)` is the deepest debt, and `Unlimited` has no floor. A PC session's unpaid remainder is `UncoveredToman`. `GraceMinuteEnabled` defaults to true: if the PC balance is still above zero but below the price of one clock minute, that minute is granted and the shortfall is a grace line (not debt, and the balance is not taken below zero). The grant is 60 seconds at the rate where that minute starts, even across midnight or a band edge, and even when the billing block is 5 or 15 minutes. Further time stays uncovered. Set the flag false to stop as soon as the next minute cannot be paid. Grace is taken off the time charge before level, birthday, and code percents. `ApplyDiscountsToPc` defaults to false: the PC debit is that post-grace amount, and the remaining-time estimate uses the full minute price. Set it true and those time discounts reduce both the debit and the estimate, and the invoice payable equals the wallet debit.

Invoice collection order is fixed: priced segments, then the grace waiver, then discounts, then the exact PC-balance and wallet shares, then one rounding of the counter remainder, then the counter payment. The PC balance pays PC time lines first. The wallet then pays what remains (one pocket in shared mode). Each of those tenders is split pro rata by largest remainder across the lines that payer may cover, and the shares add up to the amount taken. A tie goes to the earlier line. The base is the line amount after grace and discounts. `RoundingScope` chooses what the counter remainder is: the whole remainder (the default) or the time lines only, leaving buffet and services exact. The step never changes a PC or wallet debit. A fully prepaid invoice has no rounding line, and a counter remainder of zero is not lifted to the step.

`PilotSeed` is the pilot branch's starting data, not a rule in the engine. PC normal is 80,000 toman/hour and PC VIP is 110,000. Both have their own 00:00–08:30 band, at 100,000 and 130,000, and both follow special days the same way a console group does. The pilot rounding setting is on, step 5,000, direction up, and it applies only to the counter remainder. `example-normal` in the tests is still a separate fixture used where a rate needs to divide like the PS5 1-player price.

`LevelLadder` is cafe data: name, strictly ascending lifetime-hour threshold, and a percent from 0 to 100. `LevelLadder.ExampleSeed` (also `GameTimerReference.ExampleLevelSeed`) is an optional Bronze 5% … Titan 28% starter. The hour numbers on that seed are placeholders. `Resolve(lifetimeHours)` picks the highest row the customer has reached. Hours below the first threshold are no level.

`DiscountPolicy` adds the level percent and the birthday percent, caps that sum at `MaxCombinedPercent` (default **100**), and applies it to time charges only. Birthdays are Jalali. `Birthday` defaults to the exact Jalali day. `DaysOnAndAfter(7)` is that day plus the next six. `DaysAround(7)` is three days before, the day, and three days after, and can cross a Jalali month. `WholeMonth` is the whole Jalali birth month (all of Mehr, not a Gregorian month). Esfand 30 is observed on Esfand 29 when the Jalali year is not a leap year.

A discount code's `ChargeScope` defaults to time. Combine `Time`, `Buffet`, and `Services` when the code should cover more than one part. If the code also lists station groups, buffet and services are discounted only when at least one time line is in an allowed group. Otherwise the code is `not_eligible` and those lines stay full price.

`RoundingPolicy` is **off** by default. A branch turns it on with a step, a direction, and a scope.

Wallet draws do not order gift before real or the reverse. They take both as one balance.

## Rate precedence

For one instant, one group, and one tariff key `(group, mode, player count)`:

1. **Special day**, if the cafe put that Tehran date on the calendar and the date's rate table has a rate for this group and key.
2. **This group's time band**, if local time falls in one of its bands and that band has a rate for the key.
3. **Weekday**, if that day has a rate for the key.
4. **Base tariff**.

A missing rate falls through, including a special day that has no row for this player count. A special day does not need a copy of every mode, and it does not need a rate for every group. PS5's 00:00–08:30 band does not split or reprice any other group. If nothing matches, pricing throws `BillingException` with code `tariff_not_found` rather than charging zero. Only PS5 rates are live; other groups in tests use rates marked as examples.

Worked PS5 1-player figures from the owner's sheet:

| Window | Rate |
| --- | --- |
| 23:00–00:00 | 110,000 / hour → 110,000 |
| 00:00–01:30 | 160,000 / hour → 240,000 |
| **23:00–01:30, one segment** | **350,000**, two lines, no manual cut |
| 08:00–08:30 | 80,000 |
| 08:30–09:00 | 55,000 |

The band is half-open: 08:30 is already the day rate.

A special day replaces the band only for the keys it defines. Inside the band, the band replaces the weekday. They are not multiplied. Thursday 02:00–03:00 PS5 1-player is **160,000** even when Thursday has a weekday rate. Friday 10:00, outside the band, still uses the Friday rate. A 2-player key missing from the special-day table uses the band, then the weekday, then the base. A started minute is billed (partial blocks round up). That default is what the cafe confirmed; floor and nearest remain available on `BillingPolicy` if a branch ever needs them.

## Prepaid limits

`PrepaidPlanner.ForDuration` adds the play duration to the start and prices that continuous stretch, including band splits. Pauses are not predicted; a later pause extends the wall clock, not the budget. Re-price the real session when it ends.

`PrepaidPlanner.ForAmount` walks forward across the same boundaries and grants the longest prefix whose charge, under the active minute and toman rules, does not exceed the remaining budget. It will not sell a partial block that rounds up into a minute the balance cannot cover. A remainder smaller than one minute is unused and stays with the caller.

Example, PS5 1 player, budget 160,000, start 08:00:

- 08:00–08:30 at 160,000 costs 80,000.
- The remaining 80,000 buys **43 minutes** at 110,000 (78,833 toman).
- The leftover 1,167 is below one minute (1,833), so the default grace minute runs to **09:14** and waives the shortfall. The grace line stores the full minute tariff.
- A zero hourly rate is unlimited. The planner does not walk a year looking for the end.

## PC accounts

A time package stores **remaining minutes**. The face value (`2 hours = 200,000 toman`) is recorded and is not used in consumption. One package minute covers one billed minute in an allowed group, at VIP or at the ordinary rate.

Optional limits: allowed groups, an hour window `[from, until)` that may wrap midnight, and `ValidThrough` (inclusive Tehran date). Among eligible packages, the soonest expiry is spent first, then list order.

Default consumption is package minutes, then money. `MoneyThenPackages` is the other way. Money never goes below zero. A shortfall is `UncoveredToman` for the counter; it is not a negative balance. The gross, the toman value covered by packages, the money drawn, and the shortfall add up to the session charge.

If a priced line straddles a package window, settlement splits that line at the window and allocates the original toman and billed minutes in proportion to the seconds (the tail gets the rounding remainder). The session total does not change. A package with no window, or a line that already ended on the boundary, is all-or-nothing.

`RemainingTimeEstimator.Estimate` answers "if they start **now** on this group". Package minutes count 1:1 when PC play is allowed. Money converts at the rate in force at that instant only, which is why the same positive balance is a different length on an ordinary example group and on an example VIP group. At zero money the money portion of the estimate is zero. In shared mode, a negative balance with `AllowPcWhileInDebt` false sets `BlockedByDebt` and the total to zero; the packages stay stored. With the flag true, the estimate is only the package minutes and the money balance is still shown as negative. In separate mode the PC balance is what the estimate reads, even when the non-PC wallet is negative. A zero hourly rate is free: money minutes are null.

Money still due on a PC session after packages is uncovered. That balance stops at zero. Console and table invoices are what may leave their wallet negative.

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
| Exact Jalali day (**default**) | The birthday's Jalali month and day. |
| `DaysOnAndAfter(N)` | That Jalali day and the next N−1 days. A window can cross into the next Jalali month, for example 28 Mehr plus five days reaches 2 Aban. |
| `DaysAround(N)` | N days with the birthday inside. Odd N is centered. Even N puts the extra day after the birthday. |
| Whole birth month | Every day of the Jalali birth month. All of Mehr is month 7. |

A line outside the window gets the level percent only. The stored birthday is a `JalaliDate`. Convert a Gregorian birth date once with `JalaliDate.FromGregorian` before saving it. Esfand 30 is observed on Esfand 29 in a non-leap Jalali year (1375 is a leap year in `PersianCalendar`; 1404 is not).

Customer discounts apply to **time charges only**. Buffet and fixed services are not reduced by level or birthday. A 900,000 toman controller-damage fine stays 900,000.

A code is either a percent or a fixed toman amount, not both, and it is a third line. Its scope is `ChargeScope`: time (the default), buffet, services, or any combination such as time and buffet together. A fixed code cannot exceed that scope's gross. `ValidFrom` is inclusive and `ValidUntil` is exclusive, compared with `IssuedAt`. Outside the window the code is ignored (`outside_window`). A group restriction limits which time lines are discounted. Buffet and services in the scope are discounted only when at least one time line belongs to an allowed group; if none does, the code is `not_eligible` and buffet and services stay full price. A weekday restriction still filters time lines only. If the scope base is zero, the code is `not_eligible`.

If discounts would push a part below zero, the code is reduced first, then the birthday, then the level. The payable total is never negative. A 100% level plus a 50% time code on an 80,000 time charge with a 20,000 buffet item leaves the buffet: payable 20,000. The code does not eat the buffet unless its scope includes buffet.

Levels are not hard-coded. Build a `LevelLadder` with whatever rows the cafe wants. `LevelLadder.ExampleSeed` is only a starter: Bronze 5% from 0 hours, Silver 8% from 30, Gold 12% from 80, Platinum 16% from 150, Diamond 20% from 300, Titan 28% from 500. Pass `Resolve(lifetimeHours)` into the invoice. The invoice does not look the name up by itself.

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
- each wallet tender fits the pocket's debt policy, in tender order, so two draws on one wallet see the running balance

A failed wallet draw does not partially consume it. Method totals and per-tender gift/real splits are both returned.

Gift and real credit are one pocket. There is no gift-first or real-first order, and gift credit does not expire. The positive part of a tender keeps the gift ratio from the last top-up, so many small draws take the same gift as one draw of the sum. Leftover toman sit on real. A console or table charge may continue past zero on the pocket that pays it: the shared pocket, or the non-PC pocket when the branch is separate. The extra toman is debt on real credit only when the branch allows debt. `DebtPolicy.None` is the default and refuses to go below zero. `Limited` rejects a charge that would pass the floor, without a partial draw. `Unlimited` has no floor. A 30,000 tender on 10,000 gift and 50,000 real still draws 5,000 gift and 25,000 real. PC settlement never drives its pocket further negative.

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

- Tehran is computed from the UTC instant at a fixed `+03:30`. The offset on the input is not trusted, and a local `DateTime` is rejected. Historical daylight-saving dates before 2022 are out of scope.
- Sub-second ticks are truncated. A started minute is billed. That is the confirmed default.
- Unknown tariffs fail loudly. Only PS5 rates are loaded from Game Timer. Example groups in tests are labeled `example-*` and are not live prices.
- Time bands belong to one station group. Special days are Jalali: one date, or every year on a Jalali month and day. A one-off wins when both hit the same day. They replace the band for the keys they define.
- Birthdays and "whole month" use the Solar Hijri calendar via `PersianCalendar`. The stored form is `JalaliDate`. Esfand 30 is observed on Esfand 29 in a non-leap Jalali year.
- Same-rate caller splits match the unsplit session exactly, in minutes and in toman. Each line rounds `hourly × minutes / 60` once. The optional toman step then rounds the invoice total once.
- Package minutes are minutes, not "toman of face value".
- The remaining-time estimate does not look ahead to the next band. The prepaid amount planner does. In the default shared mode a negative balance blocks PC play and the estimate is zero.
- `WalletMode.Shared` is the default: one pocket. Console debt blocks PC play unless `AllowPcWhileInDebt` is on, and even then PC only spends package minutes. `WalletMode.Separate` keeps console debt off the PC balance. Top-ups name which pocket. PC settlement stops at zero either way.
- Level and birthday percents are summed and capped at 100% by default, on time only. The ladder is cafe data. Codes are separate. A group-restricted code discounts buffet and services only when a matching time line is present.
- Wallet gift and real credit move in proportion. The floored gift share can be one toman short of an even split; that toman is reported as real. Gift credit does not expire. Debt beyond the positive balance sits on real credit.
- Rounding a tiny amount up to a large step is allowed when rounding is on (100 with step 5,000 becomes 5,000). Zero stays zero.
- Malformed input throws `BillingException`. An unbalanced payment is a result with error codes, not an exception.

## Owner answers already applied

Live prices are PS5 only. Bands are per group. Every started minute is billed. Birthdays, birthday windows, and special days are Solar Hijri. Level and birthday discounts add, with a 100% cap, on time only. Levels are cafe-configurable; the Bronze…Titan list is only a seed. A group-restricted code discounts buffet and services only when a matching time line is on the invoice. PC charges stop at zero. The branch chooses a shared wallet or separate PC and non-PC balances. Console debt blocks PC play only in the shared mode, and only while `AllowPcWhileInDebt` is off.

## Still open

1. The example level seed's hour thresholds (0, 30, 80, 150, 300, 500) are placeholders. Replace them with the cafe's real ladder when you have it.
