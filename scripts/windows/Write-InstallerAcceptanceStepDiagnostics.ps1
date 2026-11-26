# CI-only: dump installer hang evidence after acceptance (or step timeout). Bounded WMI.
$ErrorActionPreference = "Continue"
$root = if ($env:GITHUB_WORKSPACE) { $env:GITHUB_WORKSPACE } else { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
$uiScript = Join-Path $root "installer/tools/acceptance/Get-CheckpointSetupUiText.ps1"
if (Test-Path -LiteralPath $uiScript) { . $uiScript }
$artifactDir = Join-Path $root "artifacts/installer/acceptance-logs"
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
$stamp = [DateTime]::UtcNow.ToString("yyyyMMddHHmmss")
$reportPath = Join-Path $artifactDir "ci-step-diagnostics-$stamp.txt"

function Add-DiagLine {
    param([string] $Line)
    Write-Host "installer-step-diagnostics: $Line"
    Add-Content -LiteralPath $reportPath -Value $Line -Encoding UTF8
}

Add-DiagLine "=== installer acceptance step diagnostics (UTC $([DateTime]::UtcNow.ToString('o'))) ==="

$innoLogs = @(Get-ChildItem -LiteralPath "C:\ProgramData\Checkpoint\logs" -Filter "acceptance-setup*.log" -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending)
if ($innoLogs.Count -eq 0) {
    $legacy = "C:\ProgramData\Checkpoint\logs\acceptance-setup.log"
    if (Test-Path -LiteralPath $legacy) { $innoLogs = @(Get-Item -LiteralPath $legacy) }
}
foreach ($logFile in $innoLogs | Select-Object -First 3) {
    Add-DiagLine "=== tail Inno /LOG: $($logFile.FullName) ==="
    @(Get-Content -LiteralPath $logFile.FullName -Tail 120 -Encoding UTF8 -ErrorAction SilentlyContinue) | ForEach-Object { Add-DiagLine "inno: $_" }
    Copy-Item -LiteralPath $logFile.FullName -Destination (Join-Path $artifactDir $logFile.Name) -Force -ErrorAction SilentlyContinue
}
if ($innoLogs.Count -eq 0) { Add-DiagLine "inno: (no acceptance-setup*.log)" }

$b5Candidates = @(
    (Join-Path $root "artifacts/installer/acceptance-logs/b5-kiosk-install.log"),
    (Join-Path "C:\ProgramData\Checkpoint\logs" "b5-kiosk-install.log")
)
foreach ($b5Path in $b5Candidates) {
    if (-not (Test-Path -LiteralPath $b5Path)) { continue }
    Add-DiagLine "=== tail Inno /LOG: $b5Path ==="
    @(Get-Content -LiteralPath $b5Path -Tail 120 -Encoding UTF8 -ErrorAction SilentlyContinue) | ForEach-Object { Add-DiagLine "inno: $_" }
    Copy-Item -LiteralPath $b5Path -Destination (Join-Path $artifactDir "b5-kiosk-install.log") -Force -ErrorAction SilentlyContinue
    break
}

# R10j (BL-rr8-1): kiosk B-7 Inno logs + CheckpointGuard service / log / event evidence.
$guardDiag = Join-Path $root "installer/tools/acceptance/Get-CheckpointGuardDiagnostics.ps1"
if (Test-Path -LiteralPath $guardDiag) {
    . $guardDiag
    $b7Logs = @("b7-kiosk-low-install.log", "b7-kiosk-upgrade.log") | ForEach-Object { Join-Path $artifactDir $_ }
    foreach ($line in (Get-CheckpointGuardDiagnosticLines -InnoLogs $b7Logs)) { Add-DiagLine $line }
} else {
    Add-DiagLine "guard: (Get-CheckpointGuardDiagnostics.ps1 missing)"
}

foreach ($svc in @("CheckpointServer", "CheckpointPostgres")) {
    Add-DiagLine "=== sc query $svc ==="
    & sc.exe query $svc 2>&1 | ForEach-Object { Add-DiagLine "sc: $_" }
}

Add-DiagLine "=== tasklist (setup-related, with window titles) ==="
& tasklist.exe /V /FO CSV 2>&1 | ForEach-Object {
    if ($_ -match 'CheckpointServerSetup|GameClub\.Server|postgres|setup\.exe|_iu|is-' -or $_ -match '"Image Name"') {
        Add-DiagLine "tasklist: $_"
    }
}

Add-DiagLine "=== Get-Process MainWindowTitle (setup/server/pg/pwsh) ==="
$patterns = @("CheckpointServerSetup*", "GameClub.Server", "postgres", "powershell", "pwsh")
foreach ($pat in $patterns) {
    try {
        Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like $pat } | ForEach-Object {
            Add-DiagLine "proc: pid=$($_.Id) name=$($_.ProcessName) mainWindow=$($_.MainWindowTitle)"
        }
    } catch {
        Add-DiagLine "proc: pattern=$pat error=$($_.Exception.Message)"
    }
}

if (Get-Command Get-CheckpointSetupUiText -ErrorAction SilentlyContinue) {
    Add-DiagLine "=== Setup visible window + child control text (UTF-8) ==="
    foreach ($line in Get-CheckpointSetupUiText) {
        Add-DiagLine "setup-ui: $line"
    }
    $uiArtifact = Join-Path $artifactDir "setup-ui-capture-$stamp.utf8.txt"
    [System.IO.File]::WriteAllLines($uiArtifact, @(Get-CheckpointSetupUiText), [System.Text.UTF8Encoding]::new($false))
    Add-DiagLine "setup-ui: wrote $uiArtifact"
}

Add-DiagLine "=== Win32_Process command lines (60s job cap) ==="
$wmiJob = Start-Job -ScriptBlock {
    Get-CimInstance Win32_Process -ErrorAction Stop |
        Where-Object {
            $_.Name -match 'CheckpointServerSetup|GameClub\.Server|postgres|setup\.exe|is-|powershell|pwsh' -or
            ($_.CommandLine -and $_.CommandLine -match 'Checkpoint|GameClub|Inno|acceptance')
        } |
        ForEach-Object {
            "wmi: pid=$($_.ProcessId) ppid=$($_.ParentProcessId) name=$($_.Name) cmd=$($_.CommandLine)"
        }
}
$completed = Wait-Job -Job $wmiJob -Timeout 60
if ($completed) {
    Receive-Job -Job $wmiJob | ForEach-Object { Add-DiagLine $_ }
} else {
    Add-DiagLine "wmi: timed out after 60s (enumeration hung)"
    Stop-Job -Job $wmiJob -Force -ErrorAction SilentlyContinue
}
Remove-Job -Job $wmiJob -Force -ErrorAction SilentlyContinue

Add-DiagLine "=== end ==="
Write-Host "installer-step-diagnostics: wrote $reportPath"