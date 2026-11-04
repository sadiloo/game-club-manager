# game-club-manager

Gaming-cafe management. Agents start at [AGENTS.md](AGENTS.md). This file is a pointer, not the requirements.

Domain terms: [docs/CONTEXT.md](docs/CONTEXT.md). Feature specs: `docs/specs/`.

The long README (28.8KB on the kiosk branch, slightly longer on the billing branch) is not on `main`. After those PRs merge, shrink it to a pointer under 2KB. Move business rules into `docs/CONTEXT.md` and the rest into `docs/overview.md` or `docs/archive/`. Do not drop a rule.

Projects once those branches merge:

- `src/GameClub.Billing/` — pricing (netstandard2.0 and net10.0)
- `src/GameClub.Kiosk.Core/` — kiosk logic tested on Linux
- `src/GameClub.Kiosk/` — WPF shell, .NET Framework 4.8, Windows only
- `src/GameClub.Kiosk.Guard/` — Windows service, .NET Framework 4.8
