param(
    [int]$GlobalRun = 0,
    [string]$LoginName = ""
)

$ErrorActionPreference = "Continue"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$artifactDir = Join-Path $env:GITHUB_WORKSPACE "artifacts"
if ([string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE)) {
    $artifactDir = Join-Path $repoRoot "artifacts"
}
$logDir = Join-Path $artifactDir "kiosk-windows-logs"

if ($GlobalRun -le 0 -and $env:WPF_STABILITY_RED_GLOBAL_RUN -match '^\d+$') {
    $GlobalRun = [int]$env:WPF_STABILITY_RED_GLOBAL_RUN
}
if ([string]::IsNullOrWhiteSpace($LoginName) -and $env:WPF_STABILITY_RED_LOGIN) {
    $LoginName = $env:WPF_STABILITY_RED_LOGIN.Trim()
}

Write-Host "=== WPF stability evidence (global_run=$GlobalRun login=$LoginName) ==="

$psql = 'C:\Program Files\PostgreSQL\16\bin\psql.exe'
if (Test-Path $psql) {
    $env:PGPASSWORD = 'postgres'
    Write-Host "--- stations (all columns) ---"
    & $psql -h 127.0.0.1 -U postgres -d gameclub_test -c 'SELECT * FROM stations ORDER BY "Id";' 2>&1 | ForEach-Object { Write-Host $_ }

  $sessionJoinSql = @'
SELECT s.*, st."Id" AS "StationJoin_Id", st."Name" AS "StationJoin_Name", st."GroupId" AS "StationJoin_GroupId",
       st."DefaultPlayMode" AS "StationJoin_DefaultPlayMode", st."DefaultPlayerCount" AS "StationJoin_DefaultPlayerCount",
       st."IsActive" AS "StationJoin_IsActive", st."OperationalState" AS "StationJoin_OperationalState"
FROM sessions s
LEFT JOIN stations st ON st."Id" = s."StationId"
ORDER BY s."StartedAtUtc" DESC NULLS LAST
LIMIT 20;
'@
    Write-Host "--- last 20 sessions with station join ---"
    & $psql -h 127.0.0.1 -U postgres -d gameclub_test -c $sessionJoinSql 2>&1 | ForEach-Object { Write-Host $_ }

    if (-not [string]::IsNullOrWhiteSpace($LoginName)) {
        $loginEsc = $LoginName.Replace("'", "''")
        Write-Host "--- session-start/bind: customer + credential for login ---"
        $loginNorm = $LoginName.ToLowerInvariant().Replace("'", "''")
        $credSql = "SELECT * FROM customer_credentials WHERE `"LoginNameNormalized`" = '$loginNorm' OR `"LoginNameDisplay`" = '$loginEsc';"
        & $psql -h 127.0.0.1 -U postgres -d gameclub_test -c $credSql 2>&1 | ForEach-Object { Write-Host $_ }
        $memberSql = @"
SELECT m.*
FROM customer_members m
INNER JOIN customer_credentials c ON c."CustomerId" = m."CustomerId"
WHERE c."LoginNameNormalized" = '$loginNorm' OR c."LoginNameDisplay" = '$loginEsc';
"@
        Write-Host "--- wallet (customer_members) for login ---"
        & $psql -h 127.0.0.1 -U postgres -d gameclub_test -c $memberSql 2>&1 | ForEach-Object { Write-Host $_ }
        $sessionsForLogin = @"
SELECT s.*, st."OperationalState" AS "StationOperationalState"
FROM sessions s
LEFT JOIN stations st ON st."Id" = s."StationId"
WHERE s."CustomerId" = (
  SELECT "CustomerId" FROM customer_credentials
  WHERE "LoginNameNormalized" = '$loginNorm' OR "LoginNameDisplay" = '$loginEsc'
  LIMIT 1)
ORDER BY s."StartedAtUtc" DESC NULLS LAST
LIMIT 20;
"@
        Write-Host "--- sessions for funded login ---"
        & $psql -h 127.0.0.1 -U postgres -d gameclub_test -c $sessionsForLogin 2>&1 | ForEach-Object { Write-Host $_ }
        Write-Host "--- PC-WPF stations (recent) ---"
        $wpfStationSql = 'SELECT * FROM stations WHERE "Id" LIKE ''PC-WPF-%'' ORDER BY "Id" DESC LIMIT 10;'
        & $psql -h 127.0.0.1 -U postgres -d gameclub_test -c $wpfStationSql 2>&1 | ForEach-Object { Write-Host $_ }
    }
} else {
    Write-Host "psql not found; skipping DB snapshot"
}

$trxPath = Join-Path $logDir "wpf-stability-run-$GlobalRun.trx"
if (Test-Path $trxPath) {
    Write-Host "--- trx for run $GlobalRun (kiosk fund/login diagnostics) ---"
    try {
        [xml]$trx = Get-Content $trxPath
        foreach ($unit in $trx.TestRun.Results.UnitTestResult) {
            $msg = $unit.Output.ErrorInfo.Message
            if ($msg) { Write-Host $msg }
            $std = $unit.Output.StdOut
            if ($std) { Write-Host $std }
        }
    } catch { }
}

foreach ($name in @("wpf-stability-server-out.log", "wpf-stability-server-err.log")) {
    $p = Join-Path $logDir $name
    if (Test-Path $p) {
        Write-Host "--- $name (session/kiosk/login tail, filtered) ---"
        Select-String -Path $p -Pattern "kiosk|Kiosk|login|Login|session|Session|credential|Credential|bind|conflict|reject|warn|error|fail" -CaseSensitive:$false -ErrorAction SilentlyContinue |
            Select-Object -Last 80 |
            ForEach-Object { Write-Host $_.Line }
    }
}

$evidenceOut = Join-Path $logDir "wpf-stability-red-evidence-$GlobalRun.log"
if ($GlobalRun -gt 0) {
    Write-Host "--- evidence also written to $evidenceOut (via CI log capture) ---"
}

exit 0