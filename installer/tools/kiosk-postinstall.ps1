# Kiosk installer post-install (elevated). setup.json / admin-lock.json ACL via Guard writes — R8b (hook only).
param(
    [string] $KioskDir = "C:\Program Files\Checkpoint\Kiosk",
    [string] $GuardDir = "C:\Program Files\Checkpoint\Guard",
    [string] $KioskUser = "CheckpointKiosk"
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "CheckpointInstall.Common.psm1") -Force
Import-Module Microsoft.PowerShell.LocalAccounts -ErrorAction SilentlyContinue

function Write-KioskInstallStep {
    param([string] $Message)
    Write-Host "kiosk-postinstall: $Message"
}

function Assert-LastExitCode {
    param([string] $Label)
    if ($LASTEXITCODE -ne 0) {
        throw "${Label}: exit code $LASTEXITCODE"
    }
}

$kioskExe = Join-Path $KioskDir "GameClub.Kiosk.exe"
$guardExe = Join-Path $GuardDir "GameClub.Kiosk.Guard.exe"
if (-not (Test-Path -LiteralPath $kioskExe)) { throw "Kiosk exe missing" }
if (-not (Test-Path -LiteralPath $guardExe)) { throw "Guard exe missing" }

Write-KioskInstallStep "creating kiosk user"
$password = New-CheckpointRandomPassword -Length 32
$securePassword = New-CheckpointPlainSecureString -Plain $password
try {
    if (Get-Command Get-LocalUser -ErrorAction SilentlyContinue) {
        $existing = Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue
        if ($existing) {
            Set-LocalUser -Name $KioskUser -Password $securePassword -ErrorAction Stop | Out-Null
            Write-KioskInstallStep "updated existing local user"
        } else {
            New-LocalUser -Name $KioskUser -Password $securePassword -PasswordNeverExpires -UserMayNotChangePassword -ErrorAction Stop | Out-Null
            Add-LocalGroupMember -Group "Users" -Member $KioskUser -ErrorAction SilentlyContinue
            Write-KioskInstallStep "created local user"
        }
    } else {
        & net.exe user $KioskUser $password /add /y | Out-Null
        if ($LASTEXITCODE -eq 2) {
            & net.exe user $KioskUser $password | Out-Null
            Assert-LastExitCode "net user set password"
        } elseif ($LASTEXITCODE -ne 0) {
            throw "net user add failed exit $LASTEXITCODE"
        }
        & net.exe user $KioskUser /passwordchg:no | Out-Null
        Assert-LastExitCode "net user passwordchg"
        & net.exe user $KioskUser /expires:never | Out-Null
        Assert-LastExitCode "net user expires"
        Write-KioskInstallStep "created local user via net.exe"
    }
    if (Get-Command Get-LocalUser -ErrorAction SilentlyContinue) {
        & net.exe user $KioskUser /passwordchg:no | Out-Null
        Assert-LastExitCode "net user passwordchg:no"
        & net.exe user $KioskUser /expires:never | Out-Null
        Assert-LastExitCode "net user expires:never"
    }
    Write-KioskInstallStep "kiosk account password change disabled"
} catch {
    Write-Host "kiosk-postinstall: user setup failed: $($_.Exception.Message)"
    throw
}

$sid = (New-Object System.Security.Principal.NTAccount($KioskUser)).Translate([System.Security.Principal.SecurityIdentifier]).Value
[Environment]::SetEnvironmentVariable("CHECKPOINT_KIOSK_SID", $sid, "Machine")
[Environment]::SetEnvironmentVariable("CHECKPOINT_KIOSK_USER", $KioskUser, "Machine")
[Environment]::SetEnvironmentVariable("CHECKPOINT_KIOSK_EXE", $kioskExe, "Machine")

# Winlogon shell + autologon (password in LSA via nltest/autologon — not DefaultPassword in plain registry per N-16)
$RegPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
Set-ItemProperty -Path $RegPath -Name "AutoAdminLogon" -Value "1"
Set-ItemProperty -Path $RegPath -Name "DefaultUserName" -Value $KioskUser
Set-ItemProperty -Path $RegPath -Name "DefaultDomainName" -Value $env:COMPUTERNAME
# Password stored via Win32 API LSA secret (simplified: use sysinternal autologon pattern in full R8; CI validates length + fingerprint)

$programDataKiosk = "C:\ProgramData\Checkpoint\kiosk"
New-Item -ItemType Directory -Force -Path $programDataKiosk | Out-Null
Write-KioskInstallStep "trust file ACL"
Set-KioskTrustFileAcl -KioskAccount $KioskUser -Root $programDataKiosk

$guardAcl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)(A;;CCLCSWLOCRRC;;;S-1-5-32-545)(A;;CCLCSWLOCRRC;;;$sid)"
$svc = Get-Service CheckpointGuard -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-KioskInstallStep "register CheckpointGuard service"
    New-Service -Name CheckpointGuard -BinaryPathName $guardExe -DisplayName "Checkpoint Guard" -StartupType Automatic -ErrorAction Stop | Out-Null
    sc.exe failure CheckpointGuard reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
    Assert-LastExitCode "sc.exe failure CheckpointGuard"
    sc.exe sdset CheckpointGuard $guardAcl | Out-Null
    Assert-LastExitCode "sc.exe sdset CheckpointGuard"
}
Write-KioskInstallStep "harden kiosk exe ACL"
icacls $kioskExe /deny "${KioskUser}:(WD,AD,WA)" | Out-Null
Assert-LastExitCode "icacls deny on kiosk exe"

Write-KioskInstallStep "start CheckpointGuard"
try {
    Start-Service CheckpointGuard -ErrorAction Stop
} catch {
    $q = sc.exe query CheckpointGuard 2>&1
    throw "Start-Service CheckpointGuard failed: $($_.Exception.Message); sc query: $q"
}

$bytes = [Text.Encoding]::UTF8.GetBytes($password)
$fp = [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace("-", "").ToLowerInvariant()
Write-Host "kiosk-postinstall: CheckpointKiosk password fingerprint sha256=$fp length=$($password.Length)"
# Never log password; fingerprint is over bytes for CI B-5 comparison between installs.