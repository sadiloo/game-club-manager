# src/GameClub.Kiosk — WPF, .NET Framework 4.8 (Windows only)
- Linux can compile this project with scripts/agent/build.sh. It cannot
  run or test it. No mono. Runtime checks happen in Windows CI.
- Leave the net48 reference-assemblies package in the csproj. It is what
  makes the compile succeed.
- Put pricing rules in src/GameClub.Billing. Put non-UI kiosk logic in
  src/GameClub.Kiosk.Core. Keep XAML and code-behind thin.
- If the spec requires kiosk UI changes: compile with build.sh, write
  "kiosk not run locally — verify in Windows CI" in the PR.
