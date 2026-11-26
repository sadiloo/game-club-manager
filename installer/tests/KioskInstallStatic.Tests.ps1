# B-5 static: kiosk .iss has no input pages; no forced shutdown /r; postinstall via ssPostInstall Exec
$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$iss = Join-Path $root "installer/kiosk/CheckpointKiosk.iss"
$wizard = Join-Path $root "installer/kiosk/KioskWizard.inc.iss"
$text = Get-Content -LiteralPath $iss -Raw
$wiz = Get-Content -LiteralPath $wizard -Raw
if ($text -match 'shutdown\.exe.*/r') {
    throw "Kiosk installer must not force reboot (shutdown /r)"
}
if ($text -notmatch 'DisableWelcomePage\s*=\s*yes') {
    throw "Kiosk installer must keep welcome page disabled (no input pages)"
}
if ($text -notmatch 'NeedsRestart\s*:=\s*False') {
    throw "Kiosk PrepareToInstall must set NeedsRestart False"
}
if ($text -match 'NeedsRestart\s*:=\s*True') {
    throw "Kiosk PrepareToInstall must not set NeedsRestart True"
}
if ($text -match 'kiosk-postinstall\.ps1[^\r\n]*Flags:[^\r\n]*postinstall') {
    throw "Kiosk kiosk-postinstall must not use [Run] postinstall flag (mandatory config runs from ssPostInstall)"
}
if ($wiz -notmatch 'procedure RunKioskPostinstallPhase') {
    throw "KioskWizard must run kiosk-postinstall from ssPostInstall"
}
if ($wiz -notmatch 'CHECKPOINT_EXIT_FAIL_KIOSK_POSTINSTALL\s*=\s*150') {
    throw "KioskWizard must define exit code 150 for postinstall failure"
}
# R10j (BL-rr8-1, product ruling): kiosk Setup reports success only with CheckpointGuard already Running.
# (1) Wait-CheckpointGuardRunning behaviour: a Guard that reports Running and then dies (the B7 CI shape) must fail.
Import-Module (Join-Path $root "installer/tools/CheckpointInstall.Common.psm1") -Force
function New-KioskGuardStatusFeed([string[]] $Seq, [bool] $Cycle = $false) {
    $state = @{ I = 0; Seq = $Seq; Cycle = $Cycle }
    return {
        $i = if ($state.Cycle) { $state.I % $state.Seq.Count } else { [Math]::Min($state.I, $state.Seq.Count - 1) }
        $state.I++
        $state.Seq[$i]
    }.GetNewClosure()
}
$fastSleep = { param([int] $Ms) Start-Sleep -Milliseconds $Ms }
$cases = @(
    @{ Name = "running stays"; Seq = @("Running"); Ok = $true; Reason = "running_stable" },
    @{ Name = "start pending then running"; Seq = @("StartPending", "StartPending", "Running"); Ok = $true; Reason = "running_stable" },
    @{ Name = "running then stopped (B7 shape)"; Seq = @("Running", "Running", "Stopped"); Ok = $false; Reason = "guard_stopped_after_start" },
    @{ Name = "running then stop pending then stopped"; Seq = @("Running", "StopPending", "Stopped"); Ok = $false; Reason = "guard_stopped_after_start" },
    @{ Name = "never leaves start pending"; Seq = @("StartPending"); Ok = $false; Reason = "guard_not_running_within_1s" },
    @{ Name = "service missing"; Seq = @("Missing"); Ok = $false; Reason = "guard_stopped_after_start" },
    @{ Name = "flapping running/start pending never stable"; Seq = @("Running", "StartPending"); Cycle = $true; Ok = $false; Reason = "guard_not_running_within_1s" }
)
foreach ($c in $cases) {
    $r = Wait-CheckpointGuardRunning -TimeoutSeconds 1 -StableSeconds 0.3 -PollMilliseconds 20 -GetStatus (New-KioskGuardStatusFeed $c.Seq ([bool]$c.Cycle)) -Sleep $fastSleep
    if ($r.Ok -ne $c.Ok -or $r.Reason -ne $c.Reason) {
        throw "KioskInstallStatic: Wait-CheckpointGuardRunning '$($c.Name)' got Ok=$($r.Ok) Reason=$($r.Reason), want Ok=$($c.Ok) Reason=$($c.Reason)"
    }
    if ($c.Ok -and $r.ElapsedMs -lt 300) { throw "KioskInstallStatic: '$($c.Name)' returned Ok before the 0.3s stability window ($($r.ElapsedMs) ms)" }
    if ($c.Reason -like "guard_not_running_within_*" -and $r.ElapsedMs -lt 1000) { throw "KioskInstallStatic: '$($c.Name)' gave up before the 1s timeout ($($r.ElapsedMs) ms)" }
}

# (2) kiosk-postinstall wiring: wait (<= 30 s) right after Start-Service and before the success line; on failure a
# clear phase/reason line and exit 150 (Inno KioskWizard passes Rc >= 128 through, so Setup exits 150).
$post = (Get-Content -LiteralPath (Join-Path $root "installer/tools/kiosk-postinstall.ps1") -Raw) -replace "`r`n", "`n"
$iStart = $post.IndexOf("Start-Service CheckpointGuard -ErrorAction Stop")
$iWait = $post.IndexOf('$guardWait = Wait-CheckpointGuardRunning -TimeoutSeconds 30 -StableSeconds 5')
$iFp = $post.IndexOf("password fingerprint sha256=")
$iGate = $post.IndexOf("`nif (-not `$guardWait.Ok) {")
$iStartEnd = $post.IndexOf("`n}`n", $iStart)
$between = if ($iStartEnd -ge 0 -and $iWait -gt $iStartEnd) { $post.Substring($iStartEnd + 3, $iWait - $iStartEnd - 3) } else { "<none>" }
if ($iStart -lt 0 -or $iWait -lt $iStart -or $iGate -lt $iWait -or $iFp -lt $iGate -or $between -match '(?m)^(?!\s*#|Write-KioskInstallStep )\S') {
    throw "KioskInstallStatic: kiosk-postinstall must call Wait-CheckpointGuardRunning -TimeoutSeconds 30 right after the Start-Service block, then gate on it, before the success line (start=$iStart wait=$iWait gate=$iGate fp=$iFp)"
}
$m = [regex]::Match($post, '(?s)\nif \(-not \$guardWait\.Ok\) \{\n(.*?)\n\}\n')
if (-not $m.Success) { throw "KioskInstallStatic: kiosk-postinstall missing 'if (-not `$guardWait.Ok) { ... }' block" }
$failBlock = $m.Groups[1].Value
if ($failBlock -notmatch 'CHECKPOINT_KIOSK_POSTINSTALL_ABORT phase=guard-running reason=\$\(\$guardWait\.Reason\)') {
    throw "KioskInstallStatic: guard wait failure must log 'CHECKPOINT_KIOSK_POSTINSTALL_ABORT phase=guard-running reason=...'"
}
if ($failBlock -notmatch '(?m)^\s*exit \$script:CheckpointSetupExitFailKioskPostinstall\s*$') {
    throw "KioskInstallStatic: guard wait failure must exit `$script:CheckpointSetupExitFailKioskPostinstall"
}
. (Join-Path $root "installer/ci/checkpoint-setup-exit-codes.ps1")
if ($script:CheckpointSetupExitFailKioskPostinstall -ne 150) { throw "KioskInstallStatic: CheckpointSetupExitFailKioskPostinstall must be 150" }
if ($wiz -notmatch '(?s)if \(Rc >= 128\) then\s*ExitProcess\(Rc\)') { throw "KioskInstallStatic: KioskWizard must pass postinstall exit codes >= 128 through" }

# (3) Execute the real wait/150 block from kiosk-postinstall in a child PowerShell with a Guard that died after start.
$blockStart = $post.IndexOf('$guardWait = Wait-CheckpointGuardRunning')
$blockEnd = $post.IndexOf("`n", $post.IndexOf('Write-KioskInstallStep "CheckpointGuard Running (stable'))
if ($blockStart -lt 0 -or $blockEnd -le $blockStart) { throw "KioskInstallStatic: kiosk-postinstall must call Wait-CheckpointGuardRunning before its stable line (start=$blockStart end=$blockEnd)" }
$block = $post.Substring($blockStart, $blockEnd - $blockStart)
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("kiosk-guard-wait-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
try {
    foreach ($scenario in @(
            @{ Name = "stopped"; Ok = '$false'; Reason = "guard_stopped_after_start"; WantExit = 150 },
            @{ Name = "running"; Ok = '$true'; Reason = "running_stable"; WantExit = 0 })) {
        $harness = @"
`$ErrorActionPreference = "Stop"
`$script:CheckpointSetupExitFailKioskPostinstall = 150
`$env:ProgramData = '$tmp'
function Write-KioskInstallStep { param([string] `$Message) Write-Host "kiosk-postinstall: `$Message" }
function Wait-CheckpointGuardRunning { param([int] `$TimeoutSeconds, [double] `$StableSeconds) Write-Host "WAIT timeout=`$TimeoutSeconds"; return @{ Ok = $($scenario.Ok); Reason = "$($scenario.Reason)"; Status = "Stopped"; ElapsedMs = 42 } }
function sc.exe { "SERVICE_NAME: CheckpointGuard STATE : 1 STOPPED" }
$block
Write-Host "AFTER-BLOCK"
exit 0
"@
        $file = Join-Path $tmp "block-$($scenario.Name).ps1"
        [System.IO.File]::WriteAllText($file, $harness, (New-Object System.Text.UTF8Encoding($true)))
        # Windows PowerShell, the host Inno runs kiosk-postinstall with (box: the Invoke-BoxStaticTest.sh shim).
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $file 2>&1 | Out-String
        $rc = $LASTEXITCODE
        if ($rc -ne $scenario.WantExit) { throw "KioskInstallStatic: postinstall guard block '$($scenario.Name)' exit $rc, want $($scenario.WantExit): $out" }
        if ($out -notmatch 'WAIT timeout=30') { throw "KioskInstallStatic: postinstall guard block '$($scenario.Name)' did not wait with timeout 30: $out" }
        if ($scenario.WantExit -eq 150) {
            if ($out -notmatch 'CHECKPOINT_KIOSK_POSTINSTALL_ABORT phase=guard-running reason=guard_stopped_after_start status=Stopped') { throw "KioskInstallStatic: abort line missing: $out" }
            if ($out -match 'AFTER-BLOCK|CheckpointGuard Running \(stable') { throw "KioskInstallStatic: postinstall continued after a Guard that is not Running: $out" }
        } elseif ($out -notmatch 'kiosk-postinstall: CheckpointGuard Running \(stable, elapsed_ms=42\)' -or $out -notmatch 'AFTER-BLOCK') {
            throw "KioskInstallStatic: running scenario must log the stable line and continue: $out"
        }
    }
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "KioskInstallStatic: guard wait ok (7 wait cases; postinstall block exit 150 on a stopped Guard, 0 when Running)"
Write-Host "KioskInstallStatic: ok"