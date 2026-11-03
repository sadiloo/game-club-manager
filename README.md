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

نرخ یک لحظه به این ترتیب انتخاب می‌شود: **تعطیل > روز هفته > بازهٔ ساعتی > نرخ پایه**. اگر برای لایهٔ بالاتر نرخی تعریف نشده باشد، به لایهٔ بعدی برمی‌گردد. سانسِ طولانی، حتی اگر از نیمه‌شب یا از مرز «ساعات خاص» (مثلاً ۰۸:۳۰) رد شود، خودش تکه‌تکه می‌شود و هر تکه با نرخ همان بازه حساب می‌شود. اپراتور لازم نیست نیمه‌شب را دستی ببرد.

پیش‌فرض تخفیف: بهترینِ «درصد سطح مشتری» یا «درصد تولد»، به‌علاوهٔ فقط یک کد تخفیف. هر دو روی مبلغ اصلی زمان بازی حساب می‌شوند، نه روی بوفه و خدمات. مبلغ نهایی منفی نمی‌شود. گرد کردن بعد از تخفیف است، برای هر شعبه روشن/خاموش می‌شود، و اختلافش یک سطر جدا در فاکتور است. در کیف پول، اول اعتبار هدیه و بعد اعتبار واقعی مصرف می‌شود. هر دو ترتیب قابل تنظیم است.

قیمت‌های PS5 از خروجی Game Timer خوانده شده است. نرخ VIP، PC، VR و میزها در آن فایل صفر بود و اینجا به‌عنوان «نامشخص» عمداً تعریف نشده است.

## Domain

| Type | Role |
| --- | --- |
| `StationGroup` | User-defined group (`معمولی`, CIP, VIP, VVIP, `ps5`, billiard, …) with a kind: PC, console, or timed table. Unlimited groups. |
| `Station` | Belongs to exactly one group. `StationDirectory` is the lookup. |
| `PlayMode` | `Offline` is priced by player/controller count 1–4. `Online` and `Vr` are a single rate; the count is recorded but does not select the tariff. |
| `TimeBand` | Half-open local interval `[start, end)`. `end <= start` wraps midnight. Bands on one card must not overlap. Gaps use the base tariff. |
| `RateCard` | Base rates, per-band rates, per-weekday rates, and one holiday rate table. |
| `HolidayCalendar` | A set of Tehran dates. Every date shares the holiday rate table. |
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
3. Split each active stretch at Tehran midnight and at every band edge.
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

`AccountPolicy.Order` is `PackagesThenMoney` by default.

`DiscountPolicy` is `BestLevelOrBirthdayPlusCode` on `TimeChargesOnly` by default.

`RoundingPolicy` is **off** by default. A branch turns it on with a step, a direction, and a scope.

`WalletDrawOrder` is `GiftThenReal` by default.

## Rate precedence

For one instant and one tariff key `(group, mode, player count)`:

1. **Holiday**, if the Tehran date is in the holiday calendar and a holiday rate exists for that key.
2. **Weekday**, if that day has a rate for the key.
3. **Time band**, if local time falls in a band that has a rate for the key.
4. **Base tariff**.

A missing rate falls through. A holiday does not need a copy of every mode: Saturday online play still uses the special-hour online rate when only the offline holiday rate was defined. If nothing matches, pricing throws `BillingException` with code `tariff_not_found` rather than charging zero.

Worked PS5 1-player figures from the owner's sheet:

| Window | Rate |
| --- | --- |
| 23:00–00:00 | 110,000 / hour → 110,000 |
| 00:00–01:30 | 160,000 / hour → 240,000 |
| **23:00–01:30, one segment** | **350,000**, two lines, no manual cut |
| 08:00–08:30 | 80,000 |
| 08:30–09:00 | 55,000 |

The band is half-open: 08:30 is already the day rate.

Weekday and holiday rates replace the band for the keys they define. They are not multiplied by it. On a Friday whose 1-player rate is 100,000, 01:00–02:00 costs 100,000 even though special hours would have been 160,000. A 2-player rate that was not overridden still uses the band.

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

`RemainingTimeEstimator.Estimate` answers "if they start **now** on this group". Eligible package minutes are counted 1:1. The money balance is converted with the rate in force at that instant only — the next band is not projected, which is why the same balance is a different number of minutes on معمولی versus VIP. A zero hourly rate is free: money minutes are null, because the balance is not what is buying the time.

The account tests use a hypothetical ordinary rate of 110,000 and a hypothetical VIP rate of 220,000, because those rows were empty in the export. With a 220,000 toman balance the ordinary estimate is 120 minutes and the VIP estimate is 60, before any package minutes.

## Invoice, discounts, rounding

`InvoiceCalculator.Calculate` runs in this order:

1. Time lines, buffet lines (`quantity × unit price`), fixed services (damage, extra person, …).
2. Discounts.
3. Rounding.
4. The payable total, which is what split payment must match.

Line amounts sum to the payable total. Discount lines are negative. A rounding line is omitted when the adjustment is zero. A zero net is not lifted to the rounding step.

### Discount stacking

Percents are integers. Each discount toman is `amount × percent / 100`, floored. Computing each percent on its own (instead of adding the percents first) keeps every line an exact toman figure; the difference versus summing percents first is at most 1 toman per extra percent.

| Policy | Rule |
| --- | --- |
| `BestLevelOrBirthdayPlusCode` (**default**) | One customer percent: the better of the level and the birthday. Birthday counts only when `IsBirthday` is set. Ties prefer the birthday label. Plus one code. Both are percents (or the code's fixed toman) of the **original** base. |
| `SumLevelBirthdayAndCode` | Level and birthday are both taken off the original base, then the code is too. This is the opt-in closest to Game Timer, which subtracted both. |
| `BestLevelOrBirthdayThenCompoundCode` | The best of level or birthday comes off first. The code is applied to what remains of the code's own base. |

Scope:

- `TimeChargesOnly` (**default**). Buffet and services are not discounted. A 900,000 toman controller-damage fine stays 900,000.
- `WholeInvoice`. The base includes buffet and services.

A code is either a percent or a fixed toman amount, not both. `ValidFrom` is inclusive and `ValidUntil` is exclusive, compared with the invoice instant the caller passes (`IssuedAt`). Outside the window the code is ignored (`outside_window`) and the rest of the invoice still stands. A group or weekday restriction applies only to matching **time** lines; a restricted code never discounts buffet. If nothing matches, the code is `not_eligible`.

If the discounts would exceed the discount base, the code is reduced first, then the birthday, then the level. The payable total is never negative. A 100% level plus a 50% code on an 80,000 time charge with a 20,000 buffet item, time-only scope, leaves the buffet: payable 20,000.

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

**Gift credit is spent before real credit.** The member keeps the balance they paid in. `RealThenGift` is the other branch policy. Gift and real are not a third discount; they are how a wallet tender is funded.

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

**Not in the export, so not invented as real prices:** PC offline/online, Xbox, VIP, ping-pong, foosball, billiard, hockey, TV, and the `No_name` device. A lookup throws `tariff_not_found`. Tests that need a second group use clearly hypothetical rates (ordinary 110,000, VIP 1-player 220,000).

**Contrast, not a default.** Game Timer computes `minutes × (hourly / 60)` in floating point and then `ROUND(amount, -2)`, nearest 100 toman. One PS5 minute is about 1,833.33 and rounds to **1,800** there. This engine charges **1,833** and does not apply a 100-toman step. Branch rounding starts at the PA-8 steps (1,000 and up) and is off until enabled. A one-minute visit is the case where the two systems disagree; the two sample visits above do not show it.

## Decisions

- Tehran is a fixed offset. Historical daylight-saving dates before 2022 are out of scope; passing one still uses `+03:30`.
- Sub-second ticks are truncated, not rounded up. Billing granularity starts at one second of input and one minute of default charge.
- Unknown tariffs fail loudly. A blank Game Timer cell is not a free hour.
- Holiday and weekday rates are flat for that date. They are not a multiplier on the night band.
- Same-rate caller splits may differ by at most the ceiling cost of **one** granularity block (at the PS5 1-player rate, 1,834 toman). Whole-minute splits of a single rate match exactly. The toman remainder of `/ 60` can move a total by 1 toman inside that bound.
- Package minutes are minutes, not "toman of face value". VIP does not burn a 2-hour package faster than an ordinary station.
- The remaining-time estimate does not look ahead to the next band. The prepaid amount planner does.
- Discount codes are judged at `IssuedAt`. Weekday and group restrictions look at each time line's own date and group, so a Friday-only code still applies to Friday play that is invoiced on Saturday.
- Customer level names are input. The Bronze…Titan ladder is a suggestion sitting on `GameTimerReference.SuggestedLevels`.
- Rounding a tiny amount up to a large step is allowed when rounding is on (100 with step 5,000 becomes 5,000). Turn rounding off, or pick a smaller step, for small counter sales. Zero stays zero.
- Malformed input (bad offset, overlap, negative price, duplicate tariff) throws `BillingException`. An unbalanced payment is a result with error codes, not an exception.

## Open questions for the owner

1. What are the real hourly and special-hour prices for VIP, CIP, معمولی PC, Xbox, VR, and the tables (billiard, ping-pong, foosball, hockey)? The export has those rows at 0.
2. Is 00:00–08:30 still the only special band, and is it the same for every group? Should a Friday night band differ from the weekday table, or is a flat Friday rate what you want?
3. Confirm precedence: a holiday rate replaces special hours for the whole holiday, it is not "holiday plus the night band".
4. Should a started minute always be billed (current default), or only full minutes?
5. Game Timer rounds the game charge to 100 toman. The new steps start at 1,000. Do you still want a 100-toman option, and should a charge smaller than the step round up to the step?
6. Game Timer subtracted **both** the level percent and the birthday percent. The default here keeps only the better one, plus one code, and does not touch buffet or damage fines. Which combination do you want, and should a code be checked at session start instead of invoice time?
7. Is the birthday discount the birthday itself or the whole birth month? The customer sheet has a birth-month column. The level rows (name, minimum hours, percent) were not in the export — please send that ladder.
8. Wallet: spend gift credit first (current default) or real credit first? Do gift credits expire, and may they be limited to certain groups the way packages are?
9. Time packages: confirm hour windows and that a minute outside the window falls straight through to the money balance. Should the remaining-time estimate look past the next band change, or is "the current rate only" what the screen should show?
10. When the balance does not cover the visit, the counter collects the shortfall and the balance stays at zero. Confirm you do not want a negative account.
11. A prepaid amount that cannot buy a full minute grants no time and leaves the money unspent. Do you want a grace minute instead?
12. Official Iranian holidays, or a list you edit per year? Should Nowruz use a different rate table from a one-day holiday?
