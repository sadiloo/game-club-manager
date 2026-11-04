# game-club-manager — agent guide

Gaming-cafe management: ASP.NET Core server (.NET 10, not in the tree yet),
WPF kiosk (.NET Framework 4.8, Windows only), billing library
(netstandard2.0 and net10.0), planned React PWA.

## Source of truth
- The spec named in your prompt (docs/specs/...) is the only source of
  requirements. If spec and code disagree, follow the spec.
- Ambiguous or contradictory spec: do not guess. See "When blocked".
- Domain terms: docs/CONTEXT.md. Past decisions: docs/decisions/.
- Do not read README.md or docs/archive/ for requirements.

## Map
- src/GameClub.Billing/       pricing and billing rules -> src/GameClub.Billing/AGENTS.md
- src/GameClub.Kiosk.Core/    kiosk logic, tested on Linux -> src/GameClub.Kiosk.Core/AGENTS.md
- src/GameClub.Kiosk/         WPF client (net48, no Linux) -> src/GameClub.Kiosk/AGENTS.md
- src/GameClub.Kiosk.Guard/   Windows service (net48, no Linux) -> src/GameClub.Kiosk.Guard/AGENTS.md
- scripts/agent/              quiet build/test helpers
- No ASP.NET Core server project exists yet. When it is added, put an
  AGENTS.md next to it: call src/GameClub.Billing for money and time
  rules, do not re-implement them; do not change API contracts the kiosk
  uses unless the spec says so (then add a "Breaking changes" section);
  at most one EF Core migration per PR, named after the spec ID.

## Commands (always use these)
- Build:  scripts/agent/build.sh <project.csproj>
- Test while iterating: scripts/agent/test.sh <tests.csproj> --filter "<expr>"
- Full test run, once at the end: scripts/agent/test.sh all
- Scripts print a short summary and the path of the full log in /tmp.
  Read the log only if the summary is not enough.

## Working rules
- Search before reading; read only the needed ranges of large files.
- Change only what the spec needs. No drive-by refactors or renames.
- Every behavior change adds or updates a test, using the spec's worked
  examples. Never delete, skip or loosen a test to get green.
- Before claiming done: build + targeted tests pass, then one full run.
- Same failure after 2 fix attempts: stop and report the cause.
- No parallel subagents or best-of-n attempts unless the prompt asks.

## When blocked
Keep the PR as draft and list the question under "Open questions" in
the PR description. Do not invent requirements.

## PR description
Summary (max 5 lines) / Spec rules implemented (by number) / Files
changed / Tests run + result / Not done / Open questions.

## Cursor Cloud specific instructions
- Linux VM. src/GameClub.Kiosk (net48 WPF) and src/GameClub.Kiosk.Guard
  (net48 Windows service) cannot be built or run here; Windows CI builds
  them. Do not install mono or reference-assembly workarounds.
- .NET SDK and packages are preinstalled by .cursor/environment.json;
  do not reinstall SDKs.
- No MCP servers are configured; use git, gh and the shell.
