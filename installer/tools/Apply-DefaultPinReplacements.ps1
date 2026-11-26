# Apply default-pin replacements during upgrade (OQ-2); pins never logged.
param(
    [Parameter(Mandatory)][string]$PinsFile,
    [string]$ServerExe = "C:\Program Files\Checkpoint\Server\GameClub.Server.exe"
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $PinsFile)) {
    throw "PinsFile missing: $PinsFile"
}
if (-not (Test-Path -LiteralPath $ServerExe)) {
    throw "Server exe missing: $ServerExe"
}

$raw = Get-Content -LiteralPath $PinsFile -Raw | ConvertFrom-Json
$entries = @()
if ($raw -is [System.Array]) {
    foreach ($item in $raw) {
        $entries += @{ username = [string]$item.username; pin = [string]$item.pin }
    }
} else {
    foreach ($prop in $raw.PSObject.Properties) {
        $entries += @{ username = $prop.Name; pin = [string]$prop.Value }
    }
}

$active = @($entries | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_.username) -and -not [string]::IsNullOrWhiteSpace($_.pin)
    })
if ($active.Count -eq 0) {
    Write-Host "Apply-DefaultPinReplacements: skip (no pin entries)"
    exit 0
}

foreach ($e in $active) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $ServerExe
    $psi.Arguments = "--set-pin $($e.username)"
    $psi.RedirectStandardInput = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.StandardInput.Write($e.pin)
    $proc.StandardInput.Close()
    $proc.WaitForExit()
    if ($proc.ExitCode -ne 0) {
        throw "--set-pin failed for $($e.username) exit $($proc.ExitCode)"
    }
}

$remaining = & $ServerExe --check-default-pins 2>&1 | Out-String
if ($remaining.Trim()) {
    throw "default pins remain after replacement: $remaining"
}
Write-Host "Apply-DefaultPinReplacements: ok count=$($entries.Count)"