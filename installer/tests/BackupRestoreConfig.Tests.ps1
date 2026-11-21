$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $root "installer\tools\CheckpointDbConfig.ps1")

$jsonDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gcm-br-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $jsonDir | Out-Null
try {
    $jsonPath = Join-Path $jsonDir "server.json"
    @{
        ConnectionStrings = @{
            Default = "Host=127.0.0.1;Port=5433;Database=gameclub;Username=postgres;Password=secret"
        }
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

    $cfg = Read-CheckpointServerJsonConfig -ServerJsonPath $jsonPath
    if ($cfg.Database -ne "gameclub" -or $cfg.Port -ne "5433") {
        Write-Error "unexpected parsed config"
    }

    $emptyDump = Join-Path $jsonDir "empty.dump"
    "" | Set-Content -LiteralPath $emptyDump
    try {
        Test-PostgresDumpReadable -DumpPath $emptyDump
        Write-Error "expected empty dump to fail"
    }
    catch {
        if ($_.Exception.Message -notmatch "empty") {
            throw
        }
    }

    Write-Host "BackupRestoreConfig: ok"
}
finally {
    Remove-Item -LiteralPath $jsonDir -Recurse -Force -ErrorAction SilentlyContinue
}