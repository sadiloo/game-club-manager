# Server installer post-install (elevated); -InstallPhase splits §1.8 status steps.
param(
    [string] $InstallMode = "install",
    [ValidateSet("install-all", "webview2", "reinstall-kept-pg", "database", "accounts", "services", "firewall", "backup", "start", "upgrade", "upgrade-pins", "upgrade-stop", "upgrade-backup", "upgrade-start")]
    [string] $InstallPhase = "install-all",
    [string] $AccountsFile = "",
    [string] $DefaultPinsFile = "",
    [string] $ServerExe = "C:\Program Files\Checkpoint\Server\GameClub.Server.exe",
    [string] $SecondaryBackupDir = "",
    # Path of the running Setup exe ({srcexe}); used only to print the exact recovery command (PO-F).
    [string] $SetupExe = ""
)

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "CheckpointInstall.Common.psm1") -Force
. (Join-Path $PSScriptRoot "checkpoint-setup-exit-codes.ps1")
. (Join-Path $PSScriptRoot "CheckpointDbConfig.ps1")

$programData = "C:\ProgramData\Checkpoint"
$configDir = Join-Path $programData "config"
$configPath = Join-Path $configDir "server.json"
$pgRoot = "C:\Program Files\Checkpoint\PostgreSQL"
$serverDir = "C:\Program Files\Checkpoint\Server"
$logsDir = Join-Path $programData "logs"
$backupsDir = Join-Path $programData "backups"
$stagingSecretsPath = Join-Path $configDir ".install-secrets.json"
$phaseTrace = Join-Path $logsDir "postinstall-phase-trace.log"
# Written by upgrade-backup only after pg_dump succeeded; cleared by Setup (InitializeSetup) on every run.
$preUpgradeDumpMarker = Join-Path $logsDir "preupgrade-dump.current"

function Test-Phase([string[]] $Names) {
    return ($InstallPhase -eq "install-all") -or ($Names -contains $InstallPhase)
}

function Get-CheckpointPreUpgradeDumpForThisRun {
    if (-not (Test-Path -LiteralPath $preUpgradeDumpMarker)) { return $null }
    $dump = ([string](Get-Content -LiteralPath $preUpgradeDumpMarker -Raw)).Trim()
    if (-not $dump) { return $null }
    if (-not (Test-Path -LiteralPath $dump)) { return $null }
    if ((Get-Item -LiteralPath $dump).Length -le 0) { return $null }
    return $dump
}

function Get-CheckpointRecoveryCommandLine {
    # Same text as ServerWizard.inc.iss CheckpointRecoveryCommandLine (PO-F; README: Recovery).
    $exe = if ($SetupExe) { $SetupExe } else { "<path of this CheckpointServerSetup exe>" }
    return "`"$exe`" /SKIPSESSIONCHECK=1 /LOG=`"C:\ProgramData\Checkpoint\logs\setup-recovery.log`""
}

function Invoke-CheckpointPostinstallAbortCleanup {
    if ($InstallMode -eq "upgrade") {
        $dump = Get-CheckpointPreUpgradeDumpForThisRun
        if (-not $dump) {
            # The upgraded binaries are already on disk; starting CheckpointServer now would migrate the
            # database with no pre-upgrade backup. Leave it stopped (F6 ruling).
            Write-Host "CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED reason=no_preupgrade_backup service=CheckpointServer action=fix the cause shown in the Setup log, then run the same Setup again; do not start CheckpointServer by hand (it would migrate the database without a backup)"
            Write-Host "CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED recovery_command=$(Get-CheckpointRecoveryCommandLine)"
            Stop-Service CheckpointServer -ErrorAction SilentlyContinue
            Add-CheckpointUtf8NoBomLine -Path $phaseTrace -Line "upgrade_restore=left_stopped reason=no_preupgrade_backup"
            return
        }
        Write-Host "CHECKPOINT_POSTINSTALL_UPGRADE_RESTORE start CheckpointServer dump=$dump"
        Add-CheckpointUtf8NoBomLine -Path $phaseTrace -Line "upgrade_restore=start dump=$dump"
        try {
            Start-Service CheckpointServer -ErrorAction Stop
        } catch {
            Write-Host "CHECKPOINT_POSTINSTALL_UPGRADE_RESTORE start failed: $($_.Exception.Message)"
        }
    } else {
        Write-Host "CHECKPOINT_POSTINSTALL_ABORT_SERVICES_STOPPED"
        Stop-Service CheckpointServer -ErrorAction SilentlyContinue
        Stop-Service CheckpointPostgres -ErrorAction SilentlyContinue
    }
}

function Exit-CheckpointPostinstallFailure {
    param(
        [Parameter(Mandatory)][string] $Phase,
        [int] $InstallerExitCode = -1,
        [string] $Message = ""
    )
    if ($InstallerExitCode -lt 0) {
        $InstallerExitCode = Get-CheckpointPostinstallPhaseExitCode -InstallPhase $Phase
    }
    Invoke-CheckpointPostinstallAbortCleanup
    Add-CheckpointUtf8NoBomLine -Path $phaseTrace -Line "phase=$Phase status=fail exit=$InstallerExitCode"
    Write-CheckpointSetupFailureRecord -Phase $Phase -InstallerExitCode $InstallerExitCode -Message $Message
    exit $InstallerExitCode
}

trap {
    $msg = $_.Exception.Message
    if ($msg -match "CHECKPOINT_ACCOUNTS_INVALID") {
        Exit-CheckpointPostinstallFailure -Phase $InstallPhase -InstallerExitCode $script:CheckpointSetupExitInvalidAccounts -Message $msg
    }
    Exit-CheckpointPostinstallFailure -Phase $InstallPhase -Message $msg
}

New-Item -ItemType Directory -Force -Path $configDir, $logsDir, $backupsDir | Out-Null

function Complete-CheckpointPostinstallPhase {
    if ($InstallPhase -ne "install-all") {
        Add-CheckpointUtf8NoBomLine -Path $phaseTrace -Line "phase=$InstallPhase status=ok"
    }
    exit 0
}

if ($InstallPhase -ne "install-all") {
    Write-Host "CHECKPOINT_POSTINSTALL_PHASE_BEGIN phase=$InstallPhase"
    Add-CheckpointUtf8NoBomLine -Path $phaseTrace -Line "phase=$InstallPhase status=begin"
}

#region CHECKPOINT_INTERNAL_TEST_HOOKS_BEGIN
if ($env:CHECKPOINT_INTERNAL_TEST_HOOKS -eq "1") {
    $failPhasePath = Join-Path $configDir ".internal-test-fail-phase"
    if (Test-Path -LiteralPath $failPhasePath) {
        $failPhase = (Get-Content -LiteralPath $failPhasePath -Raw).Trim()
        if ($failPhase -eq $InstallPhase) {
            Write-Host "CHECKPOINT_POSTINSTALL_FAILED phase=$InstallPhase (acceptance file hook)"
            Exit-CheckpointPostinstallFailure -Phase $InstallPhase -Message "internal test file hook"
        }
    }
}

if ($env:CHECKPOINT_INTERNAL_TEST_HOOKS -eq "1" -and $env:CHECKPOINT_TEST_FAIL_PHASE -eq $InstallPhase) {
    Write-Host "CHECKPOINT_POSTINSTALL_FAILED phase=$InstallPhase (internal test hook)"
    Exit-CheckpointPostinstallFailure -Phase $InstallPhase -Message "TESTFAILPHASE hook"
}
#endregion CHECKPOINT_INTERNAL_TEST_HOOKS_END

$isUpgrade = Test-Path -LiteralPath $configPath
if ($InstallMode -eq "upgrade") { $isUpgrade = $true }

$dbPassword = $null
$sessionSecret = $null
$apiKey = $null

if ($isUpgrade -and (Test-Path -LiteralPath $configPath)) {
    $existing = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $dbPassword = ($existing.ConnectionStrings.Default -split "Password=")[1].Split(';')[0]
    $sessionSecret = [string]$existing.OperatorSessionSecret
    $apiKey = [string]$existing.GAMECLUB_API_KEY
} elseif (Test-Path -LiteralPath $stagingSecretsPath) {
    $stage = Get-Content -LiteralPath $stagingSecretsPath -Raw | ConvertFrom-Json
    $dbPassword = [string]$stage.DbPassword
    $sessionSecret = [string]$stage.OperatorSessionSecret
    $apiKey = [string]$stage.GAMECLUB_API_KEY
} else {
    $dbPassword = New-CheckpointRandomPassword -Length 32
    $sessionSecret = New-CheckpointRandomSecret -Length 48
    $apiKey = New-CheckpointRandomSecret -Length 32
    @{
        DbPassword            = $dbPassword
        OperatorSessionSecret = $sessionSecret
        GAMECLUB_API_KEY      = $apiKey
    } | ConvertTo-Json | Set-Content -LiteralPath $stagingSecretsPath -Encoding UTF8
}

if ($InstallMode -eq "upgrade") {
    if (Test-Phase @("upgrade-pins", "upgrade", "install-all")) {
        if ($DefaultPinsFile -and (Test-Path -LiteralPath $DefaultPinsFile)) {
            $pinsRaw = (Get-Content -LiteralPath $DefaultPinsFile -Raw).Trim()
            if ($pinsRaw -ne '[]') {
                & (Join-Path $PSScriptRoot "Apply-DefaultPinReplacements.ps1") -PinsFile $DefaultPinsFile -ServerExe $ServerExe
            } else {
                Write-Host "server-postinstall: upgrade-pins skip (empty DEFAULTPINS)"
            }
        }
        if ($InstallPhase -eq "upgrade-pins") { Complete-CheckpointPostinstallPhase }
    }
    if (Test-Phase @("upgrade-stop", "upgrade", "install-all")) {
        Stop-Service CheckpointServer -ErrorAction SilentlyContinue
        if ($InstallPhase -eq "upgrade-stop") { Complete-CheckpointPostinstallPhase }
    }
    if (Test-Phase @("upgrade-backup", "upgrade", "install-all")) {
        $oldVer = $env:CHECKPOINT_UPGRADE_FROM
        if (-not $oldVer) { $oldVer = "unknown" }
        $stamp = Get-Date -Format "yyyyMMdd-HHmm"
        # A rerun in the same minute gets a new name (-2, -3, ...): a previous pre-upgrade dump is never overwritten.
        $preDump = Get-CheckpointNewDumpPath -Directory $backupsDir -Stem "pre-upgrade-$oldVer-$stamp"
        $env:PGPASSWORD = $dbPassword
        $pgDump = Join-Path $pgRoot "bin\pg_dump.exe"
        $run = Invoke-CheckpointPgDumpNewFile -PgDump $pgDump -OutFile $preDump -Label "upgrade-backup" `
            -PgArgs @("-Fc", "-h", "127.0.0.1", "-p", "5433", "-U", "postgres", "-d", "checkpoint")
        if ($run.ExitCode -ne 0) { throw "pre-upgrade pg_dump failed" }
        if ($run.Empty -or -not (Test-Path -LiteralPath $preDump) -or (Get-Item -LiteralPath $preDump).Length -le 0) {
            throw "pre-upgrade pg_dump produced no dump at $preDump"
        }
        Set-CheckpointUtf8NoBomFile -Path $preUpgradeDumpMarker -Content $preDump
        $env:CHECKPOINT_PREUPGRADE_DUMP = $preDump
        if ($InstallPhase -eq "upgrade-backup") { Complete-CheckpointPostinstallPhase }
    }
    if (Test-Phase @("upgrade-start", "upgrade", "install-all")) {
        if ($AccountsFile -and (Test-Path -LiteralPath $AccountsFile)) {
            $acc = Get-Content -LiteralPath $AccountsFile -Raw | ConvertFrom-Json
            if ($acc.secondaryBackupDir) {
                $SecondaryBackupDir = [string]$acc.secondaryBackupDir
                $flag = Join-Path $logsDir "backup-secondary-missing.flag"
                if (Test-Path -LiteralPath $flag) { Remove-Item -LiteralPath $flag -Force }
                & (Join-Path $PSScriptRoot "Register-CheckpointDailyBackup.ps1") -BackupScriptPath (Join-Path $PSScriptRoot "backup-daily.ps1") -SecondaryBackupDir $SecondaryBackupDir
            }
        }
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        Start-Service CheckpointServer -ErrorAction Stop
        $sw.Stop()
        $preDump = $env:CHECKPOINT_PREUPGRADE_DUMP
        if (-not $preDump) {
            $oldVer = if ($env:CHECKPOINT_UPGRADE_FROM) { $env:CHECKPOINT_UPGRADE_FROM } else { "unknown" }
            $preDump = (Get-ChildItem -Path $backupsDir -Filter "pre-upgrade-$oldVer-*.dump" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
        }
        Add-Content -LiteralPath (Join-Path $logsDir "upgrade.log") -Value "$(Get-Date -Format o) downtimeSeconds=$([int]$sw.Elapsed.TotalSeconds) preDump=$preDump"
        Complete-CheckpointPostinstallPhase
    }
}

if (Test-Phase @("webview2")) {
    $wv2 = Get-ChildItem -Path (Join-Path $serverDir "_thirdparty") -Filter "MicrosoftEdgeWebView2RuntimeInstallerX64.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($wv2) {
        $regPath = "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
        if (-not (Test-Path -LiteralPath $regPath)) {
            Write-Host "server-postinstall: installing WebView2 offline runtime"
            Start-Process -FilePath $wv2.FullName -ArgumentList "/silent", "/install" -Wait -NoNewWindow
        }
    }
    if ($InstallPhase -ne "install-all") { Complete-CheckpointPostinstallPhase }
}

if (Test-Phase @("reinstall-kept-pg")) {
    & (Join-Path $PSScriptRoot "Initialize-CheckpointPostgres.ps1") -PgRoot $pgRoot -SuperPassword $dbPassword -ReinstallKeptCluster
    $stamp = Get-Date -Format "yyyyMMdd-HHmm"
    $preDump = Get-CheckpointNewDumpPath -Directory $backupsDir -Stem "pre-reinstall-kept-$stamp"
    $env:PGPASSWORD = $dbPassword
    $pgDump = Join-Path $pgRoot "bin\pg_dump.exe"
    $run = Invoke-CheckpointPgDumpNewFile -PgDump $pgDump -OutFile $preDump -Label "reinstall-kept-pg" `
        -PgArgs @("-Fc", "-h", "127.0.0.1", "-p", "5433", "-U", "postgres", "-d", "checkpoint")
    if (-not $run.Ok) {
        # Second attempt (default database name). The first attempt's own partial output was already removed.
        $run = Invoke-CheckpointPgDumpNewFile -PgDump $pgDump -OutFile $preDump -Label "reinstall-kept-pg-retry" `
            -PgArgs @("-Fc", "-h", "127.0.0.1", "-p", "5433", "-U", "postgres")
        if (-not $run.Ok) { throw "pre-reinstall-kept pg_dump failed" }
    }
    Write-Host "server-postinstall: pre-reinstall-kept backup $preDump"
    if ($InstallPhase -ne "install-all") { Complete-CheckpointPostinstallPhase }
}

if (Test-Phase @("database")) {
    & (Join-Path $PSScriptRoot "Initialize-CheckpointPostgres.ps1") -PgRoot $pgRoot -SuperPassword $dbPassword
    if ($InstallPhase -ne "install-all") { Complete-CheckpointPostinstallPhase }
}

$initialUsers = @()
if ($AccountsFile -and (Test-Path -LiteralPath $AccountsFile)) {
    $acc = Get-Content -LiteralPath $AccountsFile -Raw | ConvertFrom-Json
    if ($acc.secondaryBackupDir) {
        $SecondaryBackupDir = [string]$acc.secondaryBackupDir
    }
}

if ((Test-Phase @("accounts")) -and -not $isUpgrade) {
    if ($AccountsFile -and (Test-Path -LiteralPath $AccountsFile)) {
        $acc = Get-Content -LiteralPath $AccountsFile -Raw | ConvertFrom-Json
        foreach ($role in @(
                @{ Key = "manager1"; Role = "Manager" },
                @{ Key = "manager2"; Role = "Manager" },
                @{ Key = "operator"; Role = "Operator" }
            )) {
            $user = $acc.$($role.Key + "User")
            $pin = $acc.$($role.Key + "Pin")
            if ([string]::IsNullOrWhiteSpace($user)) {
                throw "CHECKPOINT_ACCOUNTS_INVALID blank username for $($role.Key)"
            }
            if ([string]::IsNullOrWhiteSpace($pin)) {
                throw "CHECKPOINT_ACCOUNTS_INVALID blank pin for $($role.Key)"
            }
            if ($user -match '\s') {
                throw "CHECKPOINT_ACCOUNTS_INVALID whitespace in username for $($role.Key)"
            }
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
            if ([string]::IsNullOrWhiteSpace($hash)) { throw "CHECKPOINT_ACCOUNTS_INVALID blank pin hash for $($role.Key)" }
            $initialUsers += @{
                username = $user
                role     = $role.Role
                pinHash  = $hash.Trim()
            }
        }
    }

    $conn = "Host=127.0.0.1;Port=5433;Database=checkpoint;Username=postgres;Password=$dbPassword"
    $serverJson = [ordered]@{
        ConnectionStrings     = @{ Default = $conn }
        OperatorSessionSecret = $sessionSecret
        GAMECLUB_API_KEY      = $apiKey
        Urls                  = "http://0.0.0.0:5000"
    }
    if ($initialUsers.Count -lt 3) {
        throw "CHECKPOINT_ACCOUNTS_INVALID expected three accounts (manager1, manager2, operator); got $($initialUsers.Count)"
    }
    # DEF-13b / PO-D: the server refuses to seed without a Manager and accepts role NAMES only; fail here first.
    foreach ($u in $initialUsers) {
        if (@("Owner", "Manager", "Operator") -cnotcontains [string]$u.role) {
            throw "CHECKPOINT_ACCOUNTS_INVALID role must be a role name (Owner, Manager, Operator); got '$($u.role)'"
        }
    }
    if (@($initialUsers | Where-Object { $_.role -ceq "Manager" }).Count -lt 1) {
        throw "CHECKPOINT_ACCOUNTS_INVALID at least one Manager is required"
    }
    $serverJson.InitialUsers = $initialUsers
    $json = $serverJson | ConvertTo-Json -Depth 5
    Set-Content -LiteralPath $configPath -Value $json -Encoding UTF8
    Set-CheckpointServerJsonAcl -ConfigPath $configPath
    if ($InstallPhase -eq "accounts") { Complete-CheckpointPostinstallPhase }
}

if ($InstallMode -eq "upgrade" -and $AccountsFile -and (Test-Path -LiteralPath $AccountsFile)) {
    $acc = Get-Content -LiteralPath $AccountsFile -Raw | ConvertFrom-Json
    if ($acc.secondaryBackupDir) {
        $SecondaryBackupDir = [string]$acc.secondaryBackupDir
    }
}

if (Test-Phase @("services")) {
    $binPath = "`"$ServerExe`""
    $svc = Get-Service CheckpointServer -ErrorAction SilentlyContinue
    if (-not $svc) {
        New-Service -Name CheckpointServer -BinaryPathName $binPath -DisplayName "Checkpoint Server" -StartupType Automatic -DependsOn CheckpointPostgres -ErrorAction Stop | Out-Null
        sc.exe config CheckpointServer start= delayed-auto | Out-Null
        sc.exe failure CheckpointServer reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
        if (-not (Get-Service CheckpointServer -ErrorAction SilentlyContinue)) {
            throw "CheckpointServer service registration failed"
        }
    }
    if ($InstallPhase -ne "install-all") { Complete-CheckpointPostinstallPhase }
}

if (Test-Phase @("firewall")) {
    $ruleName = "Checkpoint Server API"
    $existingRule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    if (-not $existingRule) {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5000 -Profile Private, Domain | Out-Null
    }
    if ($InstallPhase -ne "install-all") { Complete-CheckpointPostinstallPhase }
}

if (Test-Phase @("backup")) {
    if ([string]::IsNullOrWhiteSpace($SecondaryBackupDir)) {
        Set-Content -LiteralPath (Join-Path $logsDir "backup-secondary-missing.flag") -Value "پوشهٔ دوم پشتیبان انتخاب نشده است."
    } else {
        $flag = Join-Path $logsDir "backup-secondary-missing.flag"
        if (Test-Path -LiteralPath $flag) { Remove-Item -LiteralPath $flag -Force }
    }
    & (Join-Path $PSScriptRoot "Register-CheckpointDailyBackup.ps1") -BackupScriptPath (Join-Path $PSScriptRoot "backup-daily.ps1") -SecondaryBackupDir $SecondaryBackupDir
    if ($InstallPhase -ne "install-all") { Complete-CheckpointPostinstallPhase }
}

if (Test-Phase @("start", "install-all")) {
    $pgSvc = Get-Service CheckpointPostgres -ErrorAction SilentlyContinue
    if ($pgSvc -and $pgSvc.Status -ne 'Running') {
        Start-Service CheckpointPostgres -ErrorAction Stop
    }
    Start-Service CheckpointServer -ErrorAction Stop
    if (Test-Path -LiteralPath $stagingSecretsPath) {
        Remove-Item -LiteralPath $stagingSecretsPath -Force
    }
    Clear-CheckpointSetupFailureRecord
    if ($InstallPhase -eq "start") { Complete-CheckpointPostinstallPhase }
}