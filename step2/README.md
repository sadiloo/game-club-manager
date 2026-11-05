# QA step-2 server tests (PR #4)
- `QaServerTests/` xUnit + WebApplicationFactory against the real HTTP pipeline and PostgreSQL. `./run.sh [Pr4Root]`; env `QA_PG` = base Npgsql string without Database (default 127.0.0.1:55432 user qa). Results → /workspace/qa/regress-pr4-b5de45f/results/qa-step2.{trx,log}.
- `curl/` repro scripts for a running server: `BASE_URL=http://host:port API_KEY=... ./repro-*.sh`.
- Run single: `cd QaServerTests && dotnet test --filter "FullyQualifiedName~QA_I8"`.
