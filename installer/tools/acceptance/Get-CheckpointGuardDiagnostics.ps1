# R10j (BL-rr8-1): CheckpointGuard failure evidence for B-7 (on failure) and the CI always() diagnostics step.
# Returns text lines; every probe is best effort and never throws (diagnostics must not mask the real failure).
function Get-CheckpointGuardDiagnosticLines {
    param(
        [string[]] $InnoLogs = @(),
        [int] $InnoTail = 80,
        [int] $GuardLogTail = 60,
        [int] $EventHours = 3
    )
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($log in $InnoLogs) {
        if ([string]::IsNullOrWhiteSpace($log)) { continue }
        $lines.Add("=== tail Inno /LOG: $log ===")
        try {
            if (Test-Path -LiteralPath $log) {
                @(Get-Content -LiteralPath $log -Tail $InnoTail -ErrorAction Stop) | ForEach-Object { $lines.Add("inno: $_") }
            } else {
                $lines.Add("inno: (missing)")
            }
        } catch { $lines.Add("inno: read failed: $($_.Exception.Message)") }
    }
    foreach ($verb in @("query", "qc", "qfailure")) {
        $lines.Add("=== sc $verb CheckpointGuard ===")
        # native-exit: ignored diagnostics only (best effort; the service may be missing)
        try { @(& sc.exe $verb CheckpointGuard 2>&1) | ForEach-Object { if ("$_".Trim()) { $lines.Add("sc: $_") } } } catch { $lines.Add("sc: failed: $($_.Exception.Message)") }
    }
    $programData = if ($env:ProgramData) { $env:ProgramData } else { "C:\ProgramData" }
    $guardLog = [System.IO.Path]::Combine($programData, "Checkpoint\guard\watchdog.log")
    $lines.Add("=== Guard log: $guardLog ===")
    try {
        if (Test-Path -LiteralPath $guardLog) {
            @(Get-Content -LiteralPath $guardLog -Tail $GuardLogTail -ErrorAction Stop) | ForEach-Object { $lines.Add("guard-log: $_") }
        } else {
            $lines.Add("guard-log: (missing)")
        }
    } catch { $lines.Add("guard-log: read failed: $($_.Exception.Message)") }
    $since = (Get-Date).AddHours(-$EventHours)
    $lines.Add("=== System event log: Service Control Manager entries for CheckpointGuard (last ${EventHours}h) ===")
    try {
        $sys = @(Get-WinEvent -FilterHashtable @{ LogName = "System"; ProviderName = "Service Control Manager"; StartTime = $since } -MaxEvents 400 -ErrorAction Stop |
            Where-Object { $_.Message -match 'Checkpoint ?Guard' } | Select-Object -First 40)
        if ($sys.Count -eq 0) { $lines.Add("event: (none)") }
        foreach ($e in $sys) { $lines.Add("event: $($e.TimeCreated.ToUniversalTime().ToString('o')) id=$($e.Id) $(($e.Message -replace '\s+', ' '))") }
    } catch { $lines.Add("event: query failed: $($_.Exception.Message)") }
    $lines.Add("=== Application event log: GameClub.Kiosk.Guard crashes / CheckpointGuard source (last ${EventHours}h) ===")
    try {
        $app = @(Get-WinEvent -FilterHashtable @{ LogName = "Application"; StartTime = $since } -MaxEvents 1000 -ErrorAction Stop |
            Where-Object { $_.ProviderName -eq "CheckpointGuard" -or (($_.ProviderName -match '^\.NET Runtime$|^Application Error$') -and $_.Message -match 'GameClub\.Kiosk\.Guard') } |
            Select-Object -First 20)
        if ($app.Count -eq 0) { $lines.Add("app-event: (none)") }
        foreach ($e in $app) {
            $msg = ($e.Message -replace '\s+', ' ')
            if ($msg.Length -gt 1500) { $msg = $msg.Substring(0, 1500) + "..." }
            $lines.Add("app-event: $($e.TimeCreated.ToUniversalTime().ToString('o')) provider=$($e.ProviderName) id=$($e.Id) $msg")
        }
    } catch { $lines.Add("app-event: query failed: $($_.Exception.Message)") }
    return , $lines.ToArray()
}
