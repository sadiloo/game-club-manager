$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $root "scripts\windows\Resolve-InnoSetupVersion.ps1")

$expected = [Version]"6.7.1"
$bannerOk = @"
Inno Setup 6 Command-Line Compiler
Copyright (C) 1997-2025 Jordan Russell. All rights reserved.
Compiler engine version: Inno Setup 6.7.1
"@

$decision = Resolve-InnoVersionDecision `
  -DisplayVersion "6.7.1" `
  -FileVersion "0.0.0.0" `
  -ProductVersion "0.0.0.0" `
  -BannerText $bannerOk `
  -Expected $expected
if (-not $decision.Ok) {
  Write-Error "expected matching banner to pass: $($decision.Reason)"
}

$bannerMismatch = $bannerOk -replace "6.7.1", "6.7.0"
$decisionMismatch = Resolve-InnoVersionDecision `
  -DisplayVersion "6.7.1" `
  -FileVersion "0.0.0.0" `
  -ProductVersion "0.0.0.0" `
  -BannerText $bannerMismatch `
  -Expected $expected
if ($decisionMismatch.Ok) {
  Write-Error "expected banner mismatch to fail"
}

$decisionMissing = Resolve-InnoVersionDecision `
  -DisplayVersion "6.7.1" `
  -FileVersion "0.0.0.0" `
  -ProductVersion "0.0.0.0" `
  -BannerText "Inno Setup 6 Command-Line Compiler`n" `
  -Expected $expected
if ($decisionMissing.Ok) {
  Write-Error "expected missing banner version to fail"
}

Write-Host "IsccVersionGate: ok"