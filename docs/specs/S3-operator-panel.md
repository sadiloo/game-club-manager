# MVP Step 3 — Operator panel: station grid, session control, bill screen, manual-settle queue (DRAFT for freeze)
Builds on main: billing engine (PR #1, 7d12f58) + server/DB (PR #4, 639a3c8), src/GameClub.Server + openapi.json.
Requirements page: https://cursor.com/canvas/store-41286324-57ec-4671-9dc9-ff322c90e000/6fbd2cb1-5c7e-4126-9fd6-af8ffa19e971
Rules only from the Page; no new business rules during the PR (new ones → "future rules" backlog, rows 89/90).

## In scope
1. Operator login: a personal account per operator with a username + PIN/password (hashed, no shared PIN); roles owner/manager/operator (ST-1).
2. Station grid: every active station tile shows a live status (Free / Playing / Paused / Waiting for payment / Manual settle) and, while playing, the live amount from GET /sessions/{id}/live-amount (SS-1, row 77). Tiles keep the station's default player count/mode, which the operator can change before start (SS-1).
3. Start with one tap at server time (SS-10, step-2 default 10); pause/resume for consoles only (CN-2); on a PC there is no pause (PC-14, row 32); change the player count mid-session (CN-3, PATCH player-count).
4. Bill screen for consoles (SS-17, rows 77, 82): opening it = end of play, time frozen at the open moment; it shows rate, player count, segments, unrounded payable, PA-8 rounding, +5,000 button (PA-10, rows 73/81) and payment lines; final operator confirmation stores the invoice and frees the station; the invoice then leaves the operator's main view (row 74). Closing/cancelling without confirming → the session continues as if never stopped; each cancel is audited (rows 77, 82).
5. PC invoices (PC-14, row 85): a PC session ended by logout, zero balance, or auto-close (10-minute dead-PC rule) appears on the same bill screen only to confirm payment.
6. Payment at confirm: cash, card reader, card-to-card with destination card (PA-7) and optional reference/last 4 digits, split across several lines (PA-1, PA-3); card-to-card is stored as Pending and confirmed later by hand (PA-5). Rounding applies only to the counter amount (PA-8).
7. Manual-settle queue: a list of sessions with rejected PC reports; the operator chooses (a) bill to the last accepted checkpoint (the default) or (b) an end time no later than now; both are audited (step-2 PO clarification S14).
8. Free / no-account play: a session starts and settles with no customer attached; this is the most common console case (row 76, PC-2 guest).
9. Permissions: an operator can open past invoice details read-only; only a manager changes them, with an audit entry; there is no physical delete, only void with a mandatory reason (ST-1, ST-5, row 75, RP-4 read view).
10. The server additions the panel needs (thin, no new rules): a grid/status read endpoint, bill-screen open/cancel/confirm with payment lines, the +5,000 line, the manual-settle queue list, invoice read and void, users/roles/login, an audit actor id.
11. Integration tests for the server additions, plus UI acceptance tests for the scenarios below.

## Out of scope (later steps or backlog)
- Buffet and special services on the invoice, the buffet-only station (PO-1, PO-5, PO-7, SS-18) → next slice of step 3 (3b), kept out to keep this PR small.
- Quick customer create/link CU-15, member code, customer search → slice 3b.
- Wallet, prepaid time, packages, discounts, loyalty, customer debt (step 5).
- PC kiosk (step 4); the operator panel shows PC sessions only through the server API.
- Shift open/close and cash reconciliation, reports, the repeated-cancel shift report (ST-2, PA-4, row 82 report part) → step 6; this step only writes the cancel audit rows.
- Receipt printing/SMS (PA-9), auto card-to-card via SMS (PA-6), card-reader integration (PA-2).
- Reservations and the "broken" status workflow, session transfer (SS-4), timed tables, special-day/weekday config, multi-branch, cloud, licence, mobile app notifications.

## Acceptance scenarios (Tehran time; pilot rates; rounding on, 5,000, up, counter amount only)
1. Grid: PS5-1 started 14:00 (1 player). At 14:30 its tile shows Playing and 55,000; a never-used Normal PC tile shows Free with no amount.
2. One-tap start: the operator taps a free PS5-1 tile (default 2 players) at 14:00 → session Playing, start = server time 14:00, audit row with the operator id.
3. Console pause: PS5 1 player, start 14:00, pause 14:30 (tile Paused, amount frozen at 55,000), resume 14:50, open the bill at 15:20 → 60 billable minutes = 110,000 → collect 110,000.
4. A PC tile has no pause action; a direct pause API call → 409 and time keeps running.
5. Real invoice, frozen at open: PS5 2 players, start 14:00, bill opened at 17:36 → 450,000; the operator confirms at 17:40 with cash 450,000 → the invoice is billed to 17:36 = 450,000, the station shows Free, and the invoice leaves the main view.
6. Cancel: PS5 1 player, start 14:00, bill opened 15:00 (shows 110,000) and closed without confirm → the session is still Playing with no gap; reopened at 15:30 → 165,000; the audit holds one bill-cancel row.
7. Rounding: PS5 1 player 14:00–14:50 → payable 91,667, rounding +3,333, collect 95,000; the invoice stores both 91,667 and 95,000.
8. +5,000: on scenario 5's bill (450,000), the operator presses +5,000 twice → a separate surcharge line of 10,000, collect 460,000; each press is audited (optional reason).
9. Split payment: real invoice PS5 3 players 14:00–15:45 = 280,000; payment lines cash 200,000 + card reader 80,000 → confirm succeeds; the invoice has 2 payment lines.
10. Card-to-card: Normal PC 14:00–15:00 = 80,000, paid by card-to-card to destination card "card A" with last-4 1234 → the invoice is closed, the payment is Pending, and the station is Free; later the operator marks it Confirmed → audited.
11. PC logout: Normal PC 14:00–15:00 stopped by logout → appears on the bill screen as 80,000, waiting for payment confirmation (no freeze needed); VIP PC 02:00–03:00 → 130,000.
12. Dead PC: Normal PC started 14:00, last checkpoint 15:00, auto-closed at 15:10 → appears waiting for payment with 80,000, marked "auto-close".
13. Manual-settle queue: Normal PC started 14:00 with a rejected report and last accepted checkpoint 15:00 is listed in the queue; option (a) → invoice 80,000; on a second such session, option (b) end 15:30 → 120,000; an end time later than now → rejected. Both are audited.
14. Permissions: an operator opens yesterday's invoice → read-only detail; an edit or void → 403. A manager voids it with the reason "ثبت اشتباه" → status Void, still in history, audit with before/after; a void with no reason → 400.
15. No-account play: PS5 1 player started with no customer, 14:00–15:00 → bill 110,000 paid in cash; no customer prompt blocks the confirm.

## Assumptions (PO defaults, changeable later)
1. The panel is a web UI served by GameClub.Server on the LAN; it works on desktop and touch (tap) screens.
2. Confirm is allowed only when the payment lines add up exactly to the amount to collect; cash change is shown on screen, not stored.
3. Card-to-card destination cards (PA-7) are entered by a manager in settings; this step has a plain list editor only.
4. The tile amount refreshes at least every 10 s (polling); push updates are not required.
5. Opening the bill screen is recorded server-side (the freeze time = server time at open); a confirm bills to that time even if confirmed later.
6. The step-2 static API key stays for machine calls (kiosk/sweep); people use the login from in-scope item 1.

## Open questions (safe default applied: PO default, pending Sadegh)
1. Who may manually edit a session's start/end time (SS-10 says "with permission")? Default: manager only, audited with before/after. PO default, pending Sadegh.
2. Two operators acting on the same session at once (a listed gap on the Page): default: the first confirm/cancel wins and the second gets 409 with a refresh. PO default, pending Sadegh.
3. Can an operator (not only a manager) resolve the manual-settle queue? Default: yes, audited, as in the step-2 clarification. PO default, pending Sadegh.
4. Can an operator mark a Pending card-to-card as Confirmed or Rejected? Default: yes (PA-5 says "operator"), audited. PO default, pending Sadegh.
5. Rejected card-to-card after the invoice is closed: default: the invoice stays closed and the amount appears as unpaid in a Pending/Rejected list for the manager; no automatic debt (customer debt is step 5). PO default, pending Sadegh.
