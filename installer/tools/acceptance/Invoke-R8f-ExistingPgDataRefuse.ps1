param(
    [string] $ServerSetup,
    [string] $AccountsFile
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")

function Get-PgDataTreeHash {
    $pg = "C:\ProgramData\Checkpoint\pgdata"
    if (-not (Test-Path -LiteralPath $pg)) { return $null }
    $parts = @(Get-ChildItem -LiteralPath $pg -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object FullName |
        ForEach-Object {
            $h = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            "$($_.FullName):$h"
        })
    $bytes = [Text.Encoding]::UTF8.GetBytes($parts -join "`n")
    $ms = New-Object System.IO.MemoryStream(,$bytes)
  try {
        return (Get-FileHash -Algorithm SHA256 -InputStream $ms).Hash
    } finally {
        $ms.Dispose()
    }
}

Write-Host "R8f-b: seed install for pgdata refuse pin"
$seedArgs = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/ACCOUNTS=$AccountsFile")
Start-CheckpointServerSetup -LegLabel "r8f-b-seed" -Exe $ServerSetup -ArgumentList $seedArgs -TimeoutSeconds 2700 | Out-Null

$unins = Get-ChildItem "C:\Program Files\Checkpoint\Server\unins*.exe" | Select-Object -First 1
if (-not $unins) { throw "R8f-b: uninstaller missing" }
$proc = Start-Process -FilePath $unins.FullName -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES") -Wait -PassThru
if ($proc.ExitCode -ne 0) { throw "R8f-b: uninstall exit $($proc.ExitCode)" }

$pgVer = Join-Path "C:\ProgramData\Checkpoint\pgdata" "PG_VERSION"
Set-Content -LiteralPath $pgVer -Value "99" -NoNewline -Encoding ASCII
$beforeHash = Get-PgDataTreeHash
if (-not $beforeHash) { throw "R8f-b: pgdata hash missing after junk" }

$setupArgs = @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
    "/ACCOUNTS=$AccountsFile"
)
Start-CheckpointServerSetup -LegLabel "r8f-existing-pgdata-refuse" -Exe $ServerSetup -ArgumentList $setupArgs `
    -TimeoutSeconds 600 -ExpectExitCode $script:CheckpointSetupExitExistingDataKept | Out-Null

$afterHash = Get-PgDataTreeHash
if ($beforeHash -ne $afterHash) {
    throw "R8f-b: pgdata tree hash changed before=$beforeHash after=$afterHash"
}
if (Get-Service CheckpointPostgres -ErrorAction SilentlyContinue) {
    throw "R8f-b: CheckpointPostgres registered after refuse"
}
Write-Host "R8f-b: ok"