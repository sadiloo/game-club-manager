# src/GameClub.Server — ASP.NET Core API (.NET 10, PostgreSQL)
- Build: scripts/agent/build.sh src/GameClub.Server/GameClub.Server.csproj
- Tests: scripts/agent/test.sh tests/GameClub.Server.Tests/GameClub.Server.Tests.csproj (needs PostgreSQL; set GAMECLUB_TEST_DATABASE or GAMECLUB_DATABASE)
- Call GameClub.Billing for money and time; do not change kiosk API contracts unless the spec says so.
- At most one EF migration per PR, named after the spec ID.
- API auth: static branch key in header X-Api-Key (env GAMECLUB_API_KEY).
- Times stored UTC; business logic uses Tehran +03:30 via Billing.
