# Download and verify PostgreSQL (trimmed) and WebView2 offline payloads for CI / staging.
param(
    [string] $CacheDir = "",
    [switch] $SkipIfPresent
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "CheckpointInstall.Common.psm1") -Force
$root = Get-CheckpointRepoRoot -StartDir $PSScriptRoot
$manifest = Get-CheckpointThirdPartyManifest -RepoRoot $root

if ([string]::IsNullOrWhiteSpace($CacheDir)) {
    $CacheDir = Join-Path $root "artifacts/installer/third-party-cache"
}
New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null

function Get-FileIfNeeded {
    param(
        [string] $Url,
        [string] $Dest,
        [string] $Sha256,
        [string] $Label
    )
    if ((Test-Path -LiteralPath $Dest) -and $SkipIfPresent) {
        $len = (Get-Item -LiteralPath $Dest).Length
        Write-Host "installer-fetch: $Label cache hit $Dest bytes=$len"
        if (-not [string]::IsNullOrWhiteSpace($Sha256)) {
            Assert-FileSha256 -Path $Dest -ExpectedSha256 $Sha256 -Label $Label | Out-Null
        }
        return $Dest
    }
    Write-Host "installer-fetch: downloading $Label"
    Invoke-WebRequest -Uri $Url -OutFile $Dest -UseBasicParsing
    $len = (Get-Item -LiteralPath $Dest).Length
    Write-Host "installer-fetch: $Label downloaded bytes=$len documented=$($manifest.postgresql.documentedSizeBytes)"
    Assert-FileSha256 -Path $Dest -ExpectedSha256 $Sha256 -Label $Label | Out-Null
    return $Dest
}

$pgZip = Join-Path $CacheDir "postgresql-$($manifest.postgresql.version)-windows-x64-binaries.zip"
$wv2Exe = Join-Path $CacheDir "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"

Get-FileIfNeeded -Url $manifest.postgresql.url -Dest $pgZip -Sha256 $manifest.postgresql.sha256 -Label "postgresql"
Get-FileIfNeeded -Url $manifest.webview2.url -Dest $wv2Exe -Sha256 $manifest.webview2.sha256 -Label "webview2"

$pgStage = Join-Path $root "artifacts/installer/stage-postgresql"
if (Test-Path -LiteralPath $pgStage) {
    Remove-Item -LiteralPath $pgStage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $pgStage | Out-Null
Expand-Archive -LiteralPath $pgZip -DestinationPath $pgStage -Force
$inner = Get-ChildItem -LiteralPath $pgStage -Directory | Select-Object -First 1
if (-not $inner) {
    throw "postgresql zip layout unexpected"
}
$trimRoot = Join-Path $root "artifacts/installer/stage-pg-trimmed"
if (Test-Path -LiteralPath $trimRoot) {
    Remove-Item -LiteralPath $trimRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $trimRoot | Out-Null
foreach ($sub in @("bin", "lib", "share")) {
    $src = Join-Path $inner.FullName $sub
    if (-not (Test-Path -LiteralPath $src)) {
        throw "postgresql missing $sub"
    }
    Copy-Item -LiteralPath $src -Destination (Join-Path $trimRoot $sub) -Recurse -Force
}
$pgBytes = (Get-ChildItem -LiteralPath $trimRoot -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host "installer-fetch: trimmed postgresql bytes=$pgBytes"

$wv2StageDir = Join-Path $root "artifacts/installer/stage-webview2"
New-Item -ItemType Directory -Force -Path $wv2StageDir | Out-Null
Copy-Item -LiteralPath $wv2Exe -Destination (Join-Path $wv2StageDir "MicrosoftEdgeWebView2RuntimeInstallerX64.exe") -Force
$wv2Bytes = (Get-Item -LiteralPath $wv2Exe).Length
Write-Host "installer-fetch: webview2 bytes=$wv2Bytes"

@{
    PostgreSqlTrimmedDir = $trimRoot
    WebView2Installer    = (Join-Path $root "artifacts/installer/stage-webview2/MicrosoftEdgeWebView2RuntimeInstallerX64.exe")
    PostgreSqlZipBytes   = (Get-Item -LiteralPath $pgZip).Length
    WebView2Bytes        = $wv2Bytes
    TrimmedPgBytes       = $pgBytes
}