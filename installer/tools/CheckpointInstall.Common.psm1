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
        throw "$Label sha256 must be pinned in installer/third-party/versions.json"
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

function Set-KioskTrustFileAcl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$KioskAccount,
        [string]$Root = (Join-Path $env:ProgramData 'Checkpoint\kiosk')
    )

    if (-not (Test-Path -LiteralPath $Root)) {
        New-Item -ItemType Directory -Force -Path $Root | Out-Null
    }

    $kioskSid = $null
    try {
        $kioskSid = (New-Object System.Security.Principal.NTAccount($KioskAccount)).Translate(
            [System.Security.Principal.SecurityIdentifier]).Value
    }
    catch {
        $msg = "Set-KioskTrustFileAcl: failed to resolve SID for kiosk account '$KioskAccount': $($_.Exception.Message)"
        Write-Error $msg
        throw $msg
    }

    $trustNames = @('setup.json', 'admin-lock.json')
    foreach ($name in $trustNames) {
        $path = Join-Path $Root $name
        if (-not (Test-Path -LiteralPath $path)) {
            [System.IO.File]::WriteAllText($path, '')
        }

        icacls $path /setowner "NT AUTHORITY\SYSTEM" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "icacls setowner failed for $path" }
        icacls $path /inheritance:r `
            /grant:r "SYSTEM:(F)" "Administrators:(F)" "${KioskAccount}:(R)" `
            /deny "${KioskAccount}:(W,D,WDAC,WO)" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "icacls failed for $path" }
    }

    icacls $Root /setowner "NT AUTHORITY\SYSTEM" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls setowner failed for $Root" }
    icacls $Root /inheritance:r `
        /grant:r "SYSTEM:(OI)(CI)(F)" "Administrators:(OI)(CI)(F)" "${KioskAccount}:(OI)(CI)(RX)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed for directory $Root" }
}

function Get-CheckpointIranTimeZone {
    try {
        return [System.TimeZoneInfo]::FindSystemTimeZoneById("Iran Standard Time")
    } catch {
        return [System.TimeZoneInfo]::FindSystemTimeZoneById("Asia/Tehran")
    }
}

function Get-CheckpointDailyBackupStartBoundaryUtc {
    $tehran = Get-CheckpointIranTimeZone
    $localNine = [DateTime]::SpecifyKind([DateTime]::Today.AddHours(9), [DateTimeKind]::Unspecified)
    return [System.TimeZoneInfo]::ConvertTimeToUtc($localNine, $tehran).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
}

Export-ModuleMember -Function @(
    "Get-CheckpointRepoRoot",
    "Get-CheckpointAppVersion",
    "New-CheckpointRandomSecret",
    "New-CheckpointRandomPassword",
    "Invoke-CheckpointNative",
    "Get-CheckpointThirdPartyManifest",
    "Save-FileSha256",
    "Assert-FileSha256",
    "Set-CheckpointServerJsonAcl",
    "Set-KioskTrustFileAcl",
    "Get-CheckpointDailyBackupStartBoundaryUtc",
    "Get-CheckpointIranTimeZone"
)