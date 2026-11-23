$ErrorActionPreference = "Stop"
if (-not ($IsWindows -or $env:OS -eq 'Windows_NT')) {
    Write-Host "SKIP KioskTrustFileAcl (Windows only)"
    exit 0
}

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Import-Module (Join-Path $root "installer\tools\CheckpointInstall.Common.psm1") -Force

function New-LocalStandardUser {
    param([string]$Name)
    $password = "P@ssw0rd!" + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $secure = ConvertTo-SecureString $password -AsPlainText -Force
    if (-not (Get-LocalUser -Name $Name -ErrorAction SilentlyContinue)) {
        New-LocalUser -Name $Name -Password $secure -FullName $Name -Description "R8d kiosk trust ACL probe" | Out-Null
    }
    return [PSCustomObject]@{ Name = $Name; Password = $password; Secure = $secure }
}

function Invoke-UserPowerShell {
    param(
        [string]$UserName,
        [securestring]$Password,
        [string]$Command
    )
    $cred = New-Object System.Management.Automation.PSCredential($UserName, $Password)
    return Start-Process -FilePath powershell.exe -Credential $cred -ArgumentList @('-NoProfile', '-NonInteractive', '-Command', $Command) `
        -Wait -PassThru -WindowStyle Hidden
}

$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 6)
$probeUser = "GcmTrust$suffix"
if ($probeUser.Length -gt 20) { $probeUser = $probeUser.Substring(0, 20) }
$account = New-LocalStandardUser -Name $probeUser
$trustRoot = Join-Path $env:TEMP ("gcm-trust-acl-" + [Guid]::NewGuid().ToString("N"))
try {
    Set-KioskTrustFileAcl -KioskAccount $account.Name -Root $trustRoot
    $setupPath = Join-Path $trustRoot "setup.json"
    $lockPath = Join-Path $trustRoot "admin-lock.json"

    $setupLit = $setupPath.Replace("'", "''")
    $lockLit = $lockPath.Replace("'", "''")

    $writeCmd = @"
`$ErrorActionPreference = 'Stop'
try {
  Set-Content -LiteralPath '$setupLit' -Value 'tampered' -Encoding UTF8
  exit 0
} catch [System.UnauthorizedAccessException] { exit 10 }
catch [System.IO.IOException] {
  if (`$_.Exception.Message -match '(?i)denied|unauthorized') { exit 10 }
  exit 1
}
"@
    $writeProc = Invoke-UserPowerShell -UserName $account.Name -Password $account.Secure -Command $writeCmd
    if ($writeProc.ExitCode -eq 0) { Write-Error "expected UnauthorizedAccessException writing $setupPath" }
    if ($writeProc.ExitCode -ne 10) { Write-Error "expected exit 10 (UnauthorizedAccessException) for setup write; got $($writeProc.ExitCode)" }

    $lockWriteCmd = @"
`$ErrorActionPreference = 'Stop'
try {
  Set-Content -LiteralPath '$lockLit' -Value 'tampered' -Encoding UTF8
  exit 0
} catch [System.UnauthorizedAccessException] { exit 10 }
catch [System.IO.IOException] {
  if (`$_.Exception.Message -match '(?i)denied|unauthorized') { exit 10 }
  exit 1
}
"@
    $lockWriteProc = Invoke-UserPowerShell -UserName $account.Name -Password $account.Secure -Command $lockWriteCmd
    if ($lockWriteProc.ExitCode -eq 0) { Write-Error "expected UnauthorizedAccessException writing $lockPath" }
    if ($lockWriteProc.ExitCode -ne 10) { Write-Error "expected exit 10 for admin-lock write; got $($lockWriteProc.ExitCode)" }

    $wdacCmd = @"
`$ErrorActionPreference = 'Stop'
icacls '$lockLit' /grant "${env:USERNAME}:(R)" 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { exit 10 }
exit 0
"@
    $wdacProc = Invoke-UserPowerShell -UserName $account.Name -Password $account.Secure -Command $wdacCmd
    if ($wdacProc.ExitCode -eq 0) { Write-Error "expected UnauthorizedAccessException changing ACL on $lockPath" }
    if ($wdacProc.ExitCode -ne 10) { Write-Error "expected exit 10 for admin-lock WDAC; got $($wdacProc.ExitCode)" }

    $woCmd = @"
`$ErrorActionPreference = 'Stop'
takeown /f '$lockLit' 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { exit 10 }
exit 0
"@
    $woProc = Invoke-UserPowerShell -UserName $account.Name -Password $account.Secure -Command $woCmd
    if ($woProc.ExitCode -eq 0) { Write-Error "expected UnauthorizedAccessException taking ownership of $lockPath" }
    if ($woProc.ExitCode -ne 10) { Write-Error "expected exit 10 for admin-lock WO; got $($woProc.ExitCode)" }

    $deleteCmd = @"
`$ErrorActionPreference = 'Stop'
try {
  [System.IO.File]::Delete('$lockLit')
  exit 0
} catch [System.UnauthorizedAccessException] { exit 10 }
"@
    $delProc = Invoke-UserPowerShell -UserName $account.Name -Password $account.Secure -Command $deleteCmd
    if ($delProc.ExitCode -eq 0) { Write-Error "expected UnauthorizedAccessException deleting $lockPath" }
    if ($delProc.ExitCode -ne 10) { Write-Error "expected exit 10 for admin-lock delete; got $($delProc.ExitCode)" }

    $replaceCmd = @"
`$ErrorActionPreference = 'Stop'
try {
  [System.IO.File]::Move('$lockLit', (Join-Path (Split-Path '$lockLit') 'moved-lock.json'))
  exit 0
} catch [System.UnauthorizedAccessException] { exit 10 }
"@
    $repProc = Invoke-UserPowerShell -UserName $account.Name -Password $account.Secure -Command $replaceCmd
    if ($repProc.ExitCode -eq 0) { Write-Error "expected UnauthorizedAccessException replacing $lockPath" }
    if ($repProc.ExitCode -ne 10) { Write-Error "expected exit 10 for admin-lock replace; got $($repProc.ExitCode)" }

    [System.IO.File]::WriteAllText($setupPath, '{"ok":true}')
    if ((Get-Content -LiteralPath $setupPath -Raw).Trim() -ne '{"ok":true}') {
        Write-Error "admin write to trust file failed"
    }

    $badSidErr = $null
    try {
        Set-KioskTrustFileAcl -KioskAccount "NoSuchUser_$suffix" -Root (Join-Path $env:TEMP "gcm-trust-badsid")
    } catch {
        $badSidErr = $_
    }
    if (-not $badSidErr) { Write-Error "expected loud failure when kiosk account SID is missing" }

    Write-Host "kiosk_trust_files_denied_for_standard_user_write passed"
}
finally {
    if (Test-Path -LiteralPath $trustRoot) {
        Remove-Item -LiteralPath $trustRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-LocalUser -Name $account.Name -ErrorAction SilentlyContinue
}