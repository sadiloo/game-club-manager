# Windows kiosk QA (no installer in this slice)

Reproducible scripts for Hyper-V / physical PCs. Do not claim case 1 (Win7/8.1/Home) passed without JSON evidence under `%ProgramData%\Checkpoint\evidence\`.

| Script | Purpose |
|--------|---------|
| `dev-setup.ps1` | Kiosk user, shell replace, guard service registration (no MSI) |
| `run-kiosk.ps1` | Launch kiosk against server URL + station id |
| `acceptance/case-01-transport.ps1` | Log TLS + SignalR transport |
| `acceptance/case-03-login-f6.ps1` | Five failed logins then CU-1 reset |
| `acceptance/case-12-restore-f1.ps1` | Network pull before/after dead-PC timeout |

Evidence checklist: 15 acceptance cases from `step-pc-kiosk-scope.md` — attach logs/screenshots per case; no real passwords in repo artifacts.
