# src/GameClub.Kiosk.Guard — .NET Framework 4.8 Windows service
- Never build, run, or test this net48 service on the Linux cloud VM.
  Do not try: no mono, no SDK installs, no reference-assembly workarounds.
- It is not the WPF UI. The WPF shell is src/GameClub.Kiosk and has the
  same ban. Shared logic belongs in src/GameClub.Kiosk.Core.
- If the spec requires guard changes: make the edit, write
  "kiosk guard not built locally — verify in Windows CI" in the PR, and
  rely on the Windows CI job.
