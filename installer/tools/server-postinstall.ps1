# Server installer post-install (elevated).
param(
    [string] $InstallMode = "install",
    [string] $AccountsFile = "",
    [string] $ServerExe = "C:\Program Files\Checkpoint\Server\GameClub.Server.exe",
    [string] $SecondaryBackupDir = ""
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "CheckpointInstall.Common.psm1") -Force

$programData = "C:\ProgramData\Checkpoint"
$configDir = Join-Path $programData "config"
$configPath = Join-Path $configDir "server.json"
$pgRoot = "C:\Program Files\Checkpoint\PostgreSQL"
$serverDir = "C:\Program Files\Checkpoint\Server"
$logsDir = Join-Path $programData "logs"
$backupsDir = Join-Path $programData "backups"

New-Item -ItemType Directory -Force -Path $configDir, $logsDir, $backupsDir | Out-Null

$isUpgrade = Test-Path -LiteralPath $configPath
$dbPassword = $null
$sessionSecret = $null
$apiKey = $null

if ($isUpgrade) {
    $existing = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $dbPassword = ($existing.ConnectionStrings.Default -split "Password=")[1].Split(';')[0]
    $sessionSecret = [string]$existing.OperatorSessionSecret
    $apiKey = [string]$existing.GAMECLUB_API_KEY
} else {
    $dbPassword = New-CheckpointRandomPassword -Length 32
    $sessionSecret = New-CheckpointRandomSecret -Length 48
    $apiKey = New-CheckpointRandomSecret -Length 32
}

& (Join-Path $PSScriptRoot "Initialize-CheckpointPostgres.ps1") -PgRoot $pgRoot -SuperPassword $dbPassword

$initialUsers = @()
if ($AccountsFile -and (Test-Path -LiteralPath $AccountsFile)) {
    $acc = Get-Content -LiteralPath $AccountsFile -Raw | ConvertFrom-Json
    if ($acc.secondaryBackupDir) {
        $SecondaryBackupDir = [string]$acc.secondaryBackupDir
    }
}
if (-not $isUpgrade -and $AccountsFile -and (Test-Path -LiteralPath $AccountsFile)) {
    $acc = Get-Content -LiteralPath $AccountsFile -Raw | ConvertFrom-Json
    foreach ($role in @(
            @{ Key = "manager1"; Role = "Manager" },
            @{ Key = "manager2"; Role = "Manager" },
            @{ Key = "operator"; Role = "Operator" }
        )) {
        $user = $acc.$($role.Key + "User")
        $pin = $acc.$($role.Key + "Pin")
        if (-not $user -or -not $pin) { continue }
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $ServerExe
        $psi.Arguments = "--hash-pin"
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $proc = [System.Diagnostics.Process]::Start($psi)
        $proc.StandardInput.Write($pin)
        $proc.StandardInput.Close()
        $hash = $proc.StandardOutput.ReadToEnd()
        $proc.WaitForExit()
        if ($proc.ExitCode -ne 0) { throw "--hash-pin failed for $($role.Key)" }
        $initialUsers += @{
            username = $user
            role     = $role.Role
            pinHash  = $hash.Trim()
        }
    }
}

$conn = "Host=127.0.0.1;Port=5433;Database=checkpoint;Username=postgres;Password=$dbPassword"
$serverJson = [ordered]@{
    ConnectionStrings = @{ Default = $conn }
    OperatorSessionSecret = $sessionSecret
    GAMECLUB_API_KEY      = $apiKey
    Urls                  = "http://0.0.0.0:5000"
}
if ($initialUsers.Count -gt 0) {
    $serverJson.InitialUsers = $initialUsers
} elseif (-not $isUpgrade) {
    throw "First install requires non-empty InitialUsers (accounts file missing)."
}

$json = $serverJson | ConvertTo-Json -Depth 5
Set-Content -LiteralPath $configPath -Value $json -Encoding UTF8
Set-CheckpointServerJsonAcl -ConfigPath $configPath

$binPath = "`"$ServerExe`""
$svc = Get-Service CheckpointServer -ErrorAction SilentlyContinue
if (-not $svc) {
    New-Service -Name CheckpointServer -BinaryPathName $binPath -DisplayName "Checkpoint Server" -StartupType Automatic -DependsOn CheckpointPostgres | Out-Null
    sc.exe config CheckpointServer start= delayed-auto | Out-Null
    sc.exe failure CheckpointServer reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
}

if ($InstallMode -eq "upgrade") {
    $oldVer = $env:CHECKPOINT_UPGRADE_FROM
    $stamp = Get-Date -Format "yyyyMMdd-HHmm"
    $preDump = Join-Path $backupsDir "pre-upgrade-$oldVer-$stamp.dump"
    $env:PGPASSWORD = $dbPassword
    $pgDump = Join-Path $pgRoot "bin\pg_dump.exe"
    Stop-Service CheckpointServer -ErrorAction SilentlyContinue
    & $pgDump -Fc -h 127.0.0.1 -p 5433 -U postgres -d checkpoint -f $preDump
    if ($LASTEXITCODE -ne 0) { throw "pre-upgrade pg_dump failed" }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Service CheckpointServer -ErrorAction Stop
    $sw.Stop()
    Add-Content -LiteralPath (Join-Path $logsDir "upgrade.log") -Value "$(Get-Date -Format o) downtimeSeconds=$([int]$sw.Elapsed.TotalSeconds)"
}

if ($InstallMode -eq "install") {
    Start-Service CheckpointServer -ErrorAction Stop
}

# Firewall TCP 5000 (Private + Domain); VPN adapter added when present (OQ-B5).
$ruleName = "Checkpoint Server API"
$existingRule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
if (-not $existingRule) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5000 -Profile Private, Domain | Out-Null
}

# WebView2 offline installer when runtime missing
$wv2 = Get-ChildItem -Path (Join-Path $serverDir "_thirdparty") -Filter "MicrosoftEdgeWebView2RuntimeInstallerX64.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($wv2) {
    $regPath = "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
    if (-not (Test-Path -LiteralPath $regPath)) {
        Write-Host "server-postinstall: installing WebView2 offline runtime"
        Start-Process -FilePath $wv2.FullName -ArgumentList "/silent", "/install" -Wait -NoNewWindow
    } else {
        Write-Host "server-postinstall: WebView2 already present"
    }
}

if ([string]::IsNullOrWhiteSpace($SecondaryBackupDir)) {
    Set-Content -LiteralPath (Join-Path $logsDir "backup-secondary-missing.flag") -Value "پوشهٔ دوم پشتیبان انتخاب نشده است."
}
& (Join-Path $PSScriptRoot "Register-CheckpointDailyBackup.ps1") -BackupScriptPath (Join-Path $PSScriptRoot "backup-daily.ps1") -SecondaryBackupDir $SecondaryBackupDir