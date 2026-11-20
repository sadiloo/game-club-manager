$ErrorActionPreference = "Continue"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$artifactDir = Join-Path $env:GITHUB_WORKSPACE "artifacts"
if ([string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE)) {
    $artifactDir = Join-Path $repoRoot "artifacts"
}
$logDir = Join-Path $artifactDir "kiosk-windows-logs"

Write-Host "=== WPF stability failure evidence (post-failure only) ==="

$psql = 'C:\Program Files\PostgreSQL\16\bin\psql.exe'
if (Test-Path $psql) {
    $env:PGPASSWORD = 'postgres'
    Write-Host "--- stations (all columns) ---"
    & $psql -h 127.0.0.1 -U postgres -d gameclub_test -c 'SELECT * FROM stations ORDER BY "Id";' 2>&1 | ForEach-Object { Write-Host $_ }
    Write-Host "--- last 20 sessions with station join (all session + station columns) ---"
    $sessionJoinSql = @'
SELECT s.*, st."Id" AS "StationJoin_Id", st."Name" AS "StationJoin_Name", st."GroupId" AS "StationJoin_GroupId",
       st."DefaultPlayMode" AS "StationJoin_DefaultPlayMode", st."DefaultPlayerCount" AS "StationJoin_DefaultPlayerCount",
       st."IsActive" AS "StationJoin_IsActive", st."OperationalState" AS "StationJoin_OperationalState"
FROM sessions s
LEFT JOIN stations st ON st."Id" = s."StationId"
ORDER BY s."StartedAtUtc" DESC NULLS LAST
LIMIT 20;
'@
    & $psql -h 127.0.0.1 -U postgres -d gameclub_test -c $sessionJoinSql 2>&1 | ForEach-Object { Write-Host $_ }
} else {
    Write-Host "psql not found; skipping DB snapshot"
}

foreach ($name in @("wpf-stability-server-out.log", "wpf-stability-server-err.log")) {
    $p = Join-Path $logDir $name
    if (Test-Path $p) {
        Write-Host "--- $name (full) ---"
        Get-Content $p -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
    }
}

Get-ChildItem -Path $logDir -Filter "wpf-stability-*.log" -File -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host ("--- {0} (last 300 lines) ---" -f $_.Name)
    Get-Content $_.FullName -Tail 300 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
}

Get-ChildItem -Path $artifactDir -Recurse -Filter "*.trx" -File -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Name -notmatch "wpf|Wpf|remaining_time") { return }
    Write-Host ("--- failed output: {0} ---" -f $_.FullName)
    try {
        [xml]$trx = Get-Content $_.FullName
        foreach ($unit in $trx.TestRun.Results.UnitTestResult) {
            if ($unit.outcome -ne "Failed") { continue }
            $msg = $unit.Output.ErrorInfo.Message
            if ($msg) { Write-Host $msg }
            $std = $unit.Output.StdOut
            if ($std) { Write-Host $std }
        }
    } catch { }
}

Write-Host "--- timeline hints (server ready vs funding; existing logs only) ---"
$outLog = Join-Path $logDir "wpf-stability-server-out.log"
if (Test-Path $outLog) {
    Select-String -Path $outLog -Pattern "Now listening|Application started|started" -ErrorAction SilentlyContinue | Select-Object -First 15 | ForEach-Object { Write-Host $_.Line }
}
Select-String -Path (Join-Path $artifactDir "**/*.trx") -Pattern "Diagnostics:|SetupFunded|LastTopUp|locked|shellPhase" -ErrorAction SilentlyContinue |
    Select-Object -First 20 | ForEach-Object { Write-Host $_.Line }

exit 0