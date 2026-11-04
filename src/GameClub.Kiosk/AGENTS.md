# src/GameClub.Kiosk — WPF, .NET Framework 4.8 (Windows only)
- Never build, run, or test this WPF / net48 kiosk on the Linux cloud VM.
  Do not try: no mono, no SDK installs, no reference-assembly workarounds.
- Put pricing rules in src/GameClub.Billing. Put non-UI kiosk logic in
  src/GameClub.Kiosk.Core, where Linux tests already run. Keep XAML and
  code-behind thin.
- If the spec requires kiosk UI changes: make the edit, write
  "kiosk not built locally — verify in Windows CI" in the PR, and rely
  on the Windows CI job.
