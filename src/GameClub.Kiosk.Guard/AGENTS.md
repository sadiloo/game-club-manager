# src/GameClub.Kiosk.Guard — .NET Framework 4.8 Windows service
- Linux can compile this project with scripts/agent/build.sh. It cannot
  run or test it. No mono. Runtime checks happen in Windows CI.
- Leave the net48 reference-assemblies package in the csproj.
- Shared logic belongs in src/GameClub.Kiosk.Core. The WPF shell is
  src/GameClub.Kiosk.
- If the spec requires guard changes: compile with build.sh, write
  "kiosk guard not run locally — verify in Windows CI" in the PR.
