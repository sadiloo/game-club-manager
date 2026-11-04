# game-club-manager — agent guide

Gaming-cafe management: ASP.NET Core server (.NET 10), WPF kiosk
(.NET Framework 4.8), billing library (netstandard2.0 and net10.0),
planned React PWA.

Keep replies and PR text short; do not restate tool output.

## Source of truth
- The spec named in your prompt (docs/specs/...) is the only source of
  requirements for the change you were asked to make. If spec and code
  disagree, follow the spec.
- Ambiguous or contradictory spec: do not guess. See "When blocked".
- Until docs/CONTEXT.md is filled, which happens after PR #1 and PR #2
  merge, read only README.md's business-rules sections (Domain through
  Still open). Read nothing else in README.md. Do not read docs/archive/.
- Past decisions: docs/adr/.

## Map
- src/GameClub.Server/        ASP.NET Core API (.NET 10, not created yet; tests in tests/GameClub.Server.Tests/) -> src/GameClub.Server/AGENTS.md
  Call src/GameClub.Billing for money and time. Do not change API
  contracts the kiosk uses unless the spec says so (then add a
  "Breaking changes" section). At most one EF Core migration per PR,
  named after the spec ID.
- src/GameClub.Billing/       pricing and billing rules -> src/GameClub.Billing/AGENTS.md
- src/GameClub.Kiosk.Core/    kiosk logic, tested on Linux -> src/GameClub.Kiosk.Core/AGENTS.md
- src/GameClub.Kiosk/         WPF client (net48) -> src/GameClub.Kiosk/AGENTS.md
- src/GameClub.Kiosk.Guard/   Windows service (net48) -> src/GameClub.Kiosk.Guard/AGENTS.md
- scripts/agent/              quiet build/test helpers

## Commands (always use these)
- Build:  scripts/agent/build.sh <project.csproj>
- Test while iterating: scripts/agent/test.sh <tests.csproj> --filter "<expr>"
- Full test run, once at the end: scripts/agent/test.sh all
- Linux runs net10.0 only, like CI (`dotnet test GameClub.slnx -c Release -f net10.0`). Windows CI runs net48 as well.
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
- Linux can compile the net48 kiosk and guard (scripts/agent/build.sh)
  but cannot run or test them. No mono. Runtime checks happen in Windows CI.
- .NET SDK and packages are preinstalled by .cursor/environment.json;
  do not reinstall SDKs.
- No MCP servers are configured. Use git and the shell.
