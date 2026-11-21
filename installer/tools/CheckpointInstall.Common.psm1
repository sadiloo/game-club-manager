# Shared helpers for Checkpoint installers (B).
$ErrorActionPreference = "Stop"

function Get-CheckpointRepoRoot {
    param([string] $StartDir = $PSScriptRoot)
    $dir = $StartDir
    while ($dir) {
        if (Test-Path (Join-Path $dir "GameClub.slnx")) {
            return $dir
        }
        $parent = Split-Path $dir -Parent
        if (-not $parent -or $parent -eq $dir) { break }
        $dir = $parent
    }
    throw "GameClub.slnx not found above $StartDir"
}

function Get-CheckpointAppVersion {
    param([string] $RepoRoot)
    $props = Join-Path $RepoRoot "Directory.Build.props"
    if (-not (Test-Path -LiteralPath $props)) {
        throw "Directory.Build.props missing; <Version> required for installers."
    }
    [xml] $xml = Get-Content -LiteralPath $props -Raw
    $version = $xml.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "<Version> missing in Directory.Build.props; another track must add it (e.g. 0.1.0)."
    }
    return [string]$version
}

function New-CheckpointRandomSecret {
    param([int] $Length = 32)
    $bytes = New-Object byte[] $Length
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    return [Convert]::ToBase64String($bytes)
}

function New-CheckpointRandomPassword {
    param([int] $Length = 24)
  $chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#"
    $bytes = New-Object byte[] $Length
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $sb = New-Object System.Text.StringBuilder
    foreach ($b in $bytes) {
        [void]$sb.Append($chars[$b % $chars.Length])
    }
    return $sb.ToString()
}

function Invoke-CheckpointNative {
    param(
        [string] $FilePath,
        [string[]] $ArgumentList,
        [string] $FailureMessage = "native command failed"
    )
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (exit $LASTEXITCODE): $FilePath $($ArgumentList -join ' ')"
    }
}

function Get-CheckpointThirdPartyManifest {
    param([string] $RepoRoot)
    $path = Join-Path $RepoRoot "installer/third-party/versions.json"
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing installer/third-party/versions.json"
    }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Save-FileSha256 {
    param([string] $Path)
    $hash = Get-FileHash -LiteralPath $Path -Algorithm SHA256
    return $hash.Hash.ToLowerInvariant()
}

function Assert-FileSha256 {
    param(
        [string] $Path,
        [string] $ExpectedSha256,
        [string] $Label
    )
    if ([string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        $computed = Save-FileSha256 $Path
        Write-Host "installer-third-party: $Label sha256=$computed (not pinned in manifest)"
        return $computed
    }
    $actual = Save-FileSha256 $Path
    if ($actual -ne $ExpectedSha256.ToLowerInvariant()) {
        throw "$Label sha256 mismatch expected=$ExpectedSha256 actual=$actual"
    }
    Write-Host "installer-third-party: $Label sha256 verified ($actual)"
    return $actual
}

function Set-CheckpointServerJsonAcl {
    param([string] $ConfigPath)
    if (-not (Test-Path -LiteralPath $ConfigPath)) {
        throw "server.json not found at $ConfigPath"
    }
    icacls $ConfigPath /inheritance:r /grant:r "Administrators:(F)" "SYSTEM:(F)" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "icacls failed for server.json"
    }
}

# Hook for R8b: setup.json + admin-lock.json trust ACL via Guard — not implemented in R8c (see kiosk-postinstall.ps1).

Export-ModuleMember -Function @(
    "Get-CheckpointRepoRoot",
    "Get-CheckpointAppVersion",
    "New-CheckpointRandomSecret",
    "New-CheckpointRandomPassword",
    "Invoke-CheckpointNative",
    "Get-CheckpointThirdPartyManifest",
    "Save-FileSha256",
    "Assert-FileSha256",
    "Set-CheckpointServerJsonAcl"
)