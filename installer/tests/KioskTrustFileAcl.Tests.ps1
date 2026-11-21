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
        New-LocalUser -Name $Name -Password $secure -FullName $Name -Description "R8b kiosk trust ACL probe" | Out-Null
    }
    return [PSCustomObject]@{ Name = $Name; Password = $password; Secure = $secure }
}

function Try-WriteDeniedAsUser {
    param(
        [string]$UserName,
        [securestring]$Password,
        [string]$FilePath
    )
    $cred = New-Object System.Management.Automation.PSCredential($UserName, $Password)
    $pathLit = $FilePath.Replace("'", "''")
    $ps = @"
`$ErrorActionPreference = 'Stop'
try {
  Set-Content -LiteralPath '$pathLit' -Value 'tampered' -Encoding UTF8
  exit 0
} catch {
  exit 1
}
"@
    $proc = Start-Process -FilePath powershell.exe -Credential $cred -ArgumentList @('-NoProfile', '-NonInteractive', '-Command', $ps) `
        -Wait -PassThru -WindowStyle Hidden
    return $proc.ExitCode -ne 0
}

$probeUser = "GcmTrustProbe$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$account = New-LocalStandardUser -Name $probeUser
$root = Join-Path $env:TEMP ("gcm-trust-acl-" + [Guid]::NewGuid().ToString("N"))
try {
    Set-KioskTrustFileAcl -KioskAccount $account.Name -Root $root
    $setupPath = Join-Path $root "setup.json"
    if (-not (Try-WriteDeniedAsUser -UserName $account.Name -Password $account.Secure -FilePath $setupPath)) {
        Write-Error "expected standard user write to $setupPath to be denied"
    }

    [System.IO.File]::WriteAllText($setupPath, '{"ok":true}')
    if ((Get-Content -LiteralPath $setupPath -Raw).Trim() -ne '{"ok":true}') {
        Write-Error "admin write to trust file failed"
    }

    Write-Host "kiosk_trust_files_denied_for_standard_user_write passed"
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-LocalUser -Name $account.Name -ErrorAction SilentlyContinue
}