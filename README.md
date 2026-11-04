# game-club-manager

Gaming-cafe management. Agents start at [AGENTS.md](AGENTS.md).

Until the rules move to [docs/CONTEXT.md](docs/CONTEXT.md), the business rules are the README sections from Domain through Still open. Those sections arrive when PR #1 and PR #2 merge. Nothing else in this file is a requirement.

Feature specs: `docs/specs/`. Decisions: `docs/adr/`.

- `src/GameClub.Server/` — ASP.NET Core API (.NET 10, not created yet)
- `src/GameClub.Billing/` — pricing (netstandard2.0 and net10.0)
- `src/GameClub.Kiosk.Core/` — kiosk logic tested on Linux
- `src/GameClub.Kiosk/` — WPF shell, .NET Framework 4.8, compile-only on Linux
- `src/GameClub.Kiosk.Guard/` — Windows service, .NET Framework 4.8, compile-only on Linux
