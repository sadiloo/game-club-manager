$ErrorActionPreference = "Stop"

$orphanServers = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
    $_.CommandLine -and $_.CommandLine -like "*GameClub.Server.dll*"
}
if ($orphanServers) {
    foreach ($p in $orphanServers) {
        Write-Host "ORPHAN GameClub.Server pid=$($p.ProcessId) cmd=$($p.CommandLine)"
    }
    throw "Orphan GameClub.Server process(es) detected after kiosk integration."
}

$psql = "C:\Program Files\PostgreSQL\16\bin\psql.exe"
if (-not (Test-Path $psql)) {
    Write-Host "SKIP pg_stat_activity orphan check (psql not found)"
    exit 0
}

$conn = $env:GAMECLUB_TEST_DATABASE
if ([string]::IsNullOrWhiteSpace($conn)) {
    $conn = "Host=localhost;Port=5432;Database=gameclub_test_ci;Username=postgres;Password=postgres;SSL Mode=Disable"
}
if ($conn -match 'Password=([^;]+)') {
    $env:PGPASSWORD = $Matches[1]
}
elseif (-not $env:PGPASSWORD) {
    $env:PGPASSWORD = "postgres"
}
$db = if ($conn -match 'Database=([^;]+)') { $Matches[1] } else { "gameclub_test_ci" }
$hostName = if ($conn -match 'Host=([^;]+)') { $Matches[1] } else { "127.0.0.1" }
$port = if ($conn -match 'Port=([^;]+)') { $Matches[1] } else { "5432" }
$user = if ($conn -match 'Username=([^;]+)') { $Matches[1] } else { "postgres" }

$query = @"
SELECT pid, state, query
FROM pg_stat_activity
WHERE datname = '$db'
  AND pid <> pg_backend_pid()
  AND state <> 'idle'
  AND query NOT ILIKE '%pg_stat_activity%'
"@
$rows = & $psql -h $hostName -p $port -U $user -d $db -c $query 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "pg_stat_activity probe failed: $rows"
}

$text = ($rows | Out-String)
if ($text -match '\(\d+ rows\)') {
    $match = [regex]::Match($text, '\((\d+) rows\)')
    if ($match.Success -and [int]$match.Groups[1].Value -gt 0) {
        Write-Host $text
        throw "Non-idle PostgreSQL writer(s) still active after kiosk integration."
    }
}

Write-Host "orphan-check: no GameClub.Server orphans; pg_stat_activity idle."