# CI-only: dump installer hang evidence after acceptance (or step timeout). Bounded WMI.
$ErrorActionPreference = "Continue"
$root = if ($env:GITHUB_WORKSPACE) { $env:GITHUB_WORKSPACE } else { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
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

$innoLog = "C:\ProgramData\Checkpoint\logs\acceptance-setup.log"
Add-DiagLine "=== tail Inno /LOG: $innoLog ==="
if (Test-Path -LiteralPath $innoLog) {
    $tail = @(Get-Content -LiteralPath $innoLog -Tail 120 -ErrorAction SilentlyContinue)
    foreach ($line in $tail) { Add-DiagLine "inno: $line" }
    Copy-Item -LiteralPath $innoLog -Destination (Join-Path $artifactDir "acceptance-setup.log") -Force -ErrorAction SilentlyContinue
} else {
    Add-DiagLine "inno: (missing)"
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