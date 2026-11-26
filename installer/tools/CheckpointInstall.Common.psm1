# Shared helpers for Checkpoint installers (B).
$ErrorActionPreference = "Stop"

function Get-CheckpointUtf8NoBomEncoding {
    return New-Object System.Text.UTF8Encoding $false
}

function Set-CheckpointUtf8NoBomFile {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Content
    )
    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    [System.IO.File]::WriteAllText($Path, $Content, (Get-CheckpointUtf8NoBomEncoding))
}

function Add-CheckpointUtf8NoBomLine {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Line
    )
    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    $enc = Get-CheckpointUtf8NoBomEncoding
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
    try {
        $sw = New-Object System.IO.StreamWriter($fs, $enc)
        $sw.WriteLine($Line)
        $sw.Flush()
    } finally {
        $fs.Close()
    }
}

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

function New-CheckpointPlainSecureString {
    param([Parameter(Mandatory)][string] $Plain)
    $secure = New-Object System.Security.SecureString
    foreach ($ch in $Plain.ToCharArray()) {
        [void]$secure.AppendChar($ch)
    }
    $secure.MakeReadOnly()
    return $secure
}

function Set-CheckpointWinlogonAutoLogonPassword {
    param([Parameter(Mandatory)][string] $Password)
    if (-not ('CheckpointInstallWinlogonLsa' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CheckpointInstallWinlogonLsa {
    const int PolicyCreateSecret = 0x00000020;
    [StructLayout(LayoutKind.Sequential)]
    struct LsaUnicodeString {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct LsaObjectAttributes {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern uint LsaOpenPolicy(IntPtr SystemName, ref LsaObjectAttributes ObjectAttributes, int DesiredAccess, out IntPtr PolicyHandle);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern uint LsaStorePrivateData(IntPtr PolicyHandle, IntPtr KeyName, IntPtr PrivateData);
    [DllImport("advapi32.dll")]
    static extern uint LsaClose(IntPtr ObjectHandle);
    static LsaUnicodeString NewLsaString(string value) {
        var s = new LsaUnicodeString();
        s.Buffer = Marshal.StringToHGlobalUni(value);
        s.Length = (ushort)(value.Length * 2);
        s.MaximumLength = (ushort)(s.Length + 2);
        return s;
    }
    public static void StoreDefaultPassword(string password) {
        var attrs = new LsaObjectAttributes();
        attrs.Length = Marshal.SizeOf(typeof(LsaObjectAttributes));
        IntPtr policy;
        uint err = LsaOpenPolicy(IntPtr.Zero, ref attrs, PolicyCreateSecret, out policy);
        if (err != 0) { throw new InvalidOperationException("LsaOpenPolicy: " + err); }
        try {
            var keyName = NewLsaString("DefaultPassword");
            IntPtr keyPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(LsaUnicodeString)));
            Marshal.StructureToPtr(keyName, keyPtr, false);
            var secret = NewLsaString(password);
            IntPtr secretPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(LsaUnicodeString)));
            Marshal.StructureToPtr(secret, secretPtr, false);
            err = LsaStorePrivateData(policy, keyPtr, secretPtr);
            if (err != 0) { throw new InvalidOperationException("LsaStorePrivateData: " + err); }
        } finally {
            LsaClose(policy);
        }
    }
}
'@
    }
    [CheckpointInstallWinlogonLsa]::StoreDefaultPassword($Password)
}

function Test-CheckpointKioskPasswordLocked {
    param([string] $AccountName = "CheckpointKiosk")
    $localUser = Get-LocalUser -Name $AccountName -ErrorAction SilentlyContinue
    if ($localUser -and (-not $localUser.UserMayChangePassword) -and $localUser.PasswordNeverExpires) {
        return $true
    }
    $net = (& net.exe user $AccountName 2>&1 | Out-String)
    $changeBlocked = ($net -match '(?i)Password changeable\s+No') -or ($net -match '(?i)User may change password\s+No')
    $neverExpires = ($localUser -and $localUser.PasswordNeverExpires) -or ($net -match '(?i)Password expires\s+Never')
    return $changeBlocked -and $neverExpires
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

function Get-CheckpointSetupFailureIniPath {
    return Join-Path $env:ProgramData "Checkpoint\logs\checkpoint-setup-failure.ini"
}

function Clear-CheckpointSetupFailureRecord {
    $path = Get-CheckpointSetupFailureIniPath
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}

function Write-CheckpointSetupFailureRecord {
    param(
        [Parameter(Mandatory)][string] $Phase,
        [Parameter(Mandatory)][int] $InstallerExitCode,
        [string] $Message = ""
    )
    $logsDir = Join-Path $env:ProgramData "Checkpoint\logs"
    New-Item -ItemType Directory -Force -Path $logsDir | Out-Null
    $path = Get-CheckpointSetupFailureIniPath
    $lines = @(
        "installerExitCode=$InstallerExitCode",
        "phase=$Phase",
        "message=$($Message -replace '[\r\n]', ' ')"
    )
    Set-CheckpointUtf8NoBomFile -Path $path -Content ($lines -join "`n")
    Write-Host "CHECKPOINT_SETUP_FAILURE recorded exit=$InstallerExitCode phase=$Phase"
}

function Wait-CheckpointGuardRunning {
    <#
    R10j (BL-rr8-1, product ruling): kiosk Setup may report success only when CheckpointGuard is already Running;
    the 60 s service recovery is not acceptable. Start-Service returns as soon as the SCM sees SERVICE_RUNNING,
    which a Guard that dies right after OnStart also reaches, so a single Running sample is not enough: wait up to
    TimeoutSeconds for Running to hold for StableSeconds. Stopped after the start request fails at once.
    Returns @{ Ok; Reason; Status; ElapsedMs }. GetStatus/Sleep are injectable for the box static test.
    #>
    param(
        [int] $TimeoutSeconds = 30,
        [double] $StableSeconds = 5,
        [int] $PollMilliseconds = 500,
        [scriptblock] $GetStatus = { $s = Get-Service CheckpointGuard -ErrorAction SilentlyContinue; if ($s) { [string]$s.Status } else { "Missing" } },
        [scriptblock] $Sleep = { param([int] $Ms) Start-Sleep -Milliseconds $Ms }
    )
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $runningSinceMs = -1
    $status = "Unknown"
    while ($true) {
        $status = [string](& $GetStatus)
        $now = [int]$clock.ElapsedMilliseconds
        if ($status -eq "Running") {
            if ($runningSinceMs -lt 0) { $runningSinceMs = $now }
            if (($now - $runningSinceMs) -ge ($StableSeconds * 1000)) {
                return @{ Ok = $true; Reason = "running_stable"; Status = $status; ElapsedMs = $now }
            }
        } else {
            $runningSinceMs = -1
            if ($status -eq "Stopped" -or $status -eq "Missing") {
                return @{ Ok = $false; Reason = "guard_stopped_after_start"; Status = $status; ElapsedMs = $now }
            }
        }
        if ($now -ge ($TimeoutSeconds * 1000)) {
            return @{ Ok = $false; Reason = "guard_not_running_within_${TimeoutSeconds}s"; Status = $status; ElapsedMs = $now }
        }
        & $Sleep $PollMilliseconds
    }
}

Export-ModuleMember -Function @(
    "Get-CheckpointUtf8NoBomEncoding",
    "Set-CheckpointUtf8NoBomFile",
    "Add-CheckpointUtf8NoBomLine",
    "New-CheckpointPlainSecureString",
    "Test-CheckpointKioskPasswordLocked",
    "Set-CheckpointWinlogonAutoLogonPassword",
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
    "Get-CheckpointIranTimeZone",
    "Get-CheckpointSetupFailureIniPath",
    "Clear-CheckpointSetupFailureRecord",
    "Write-CheckpointSetupFailureRecord",
    "Wait-CheckpointGuardRunning"
)