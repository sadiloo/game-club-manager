# Dot-sourced from ci-installer.ps1 — requires $acceptance, $accounts, $logRoot, setup paths, versions.
$ErrorActionPreference = "Stop"

& (Join-Path $acceptance "Reset-InstallerAcceptanceMachine.ps1")

& (Join-Path $acceptance "Invoke-B2-Upgrade.ps1") -LowSetup $serverLow -HighSetup $serverHigh -AccountsFile $accounts -LowVersion $lowVersion -HighVersion $appVersion -Mode WithoutOpenSessions
& (Join-Path $acceptance "Invoke-B2-Upgrade.ps1") -LowSetup $serverHigh -HighSetup $serverHigh2 -AccountsFile $accounts -LowVersion $appVersion -HighVersion $high2Version -Mode WithOpenSessions -SkipDowngradeCheck
& (Join-Path $acceptance "Invoke-B1-CleanServerInstall.ps1") -ServerSetup $serverHigh -AccountsFile $accounts -LogsDir $logRoot
& (Join-Path $acceptance "Invoke-B3-BackupNeverDeletes.ps1") -LogsDir $logRoot
& (Join-Path $acceptance "Invoke-B4-UninstallReinstall.ps1") -ServerSetup $serverHigh -AccountsFile $accounts
& (Join-Path $acceptance "Invoke-B5-KioskInstall.ps1") -KioskSetup $kioskHigh -LogsDir $logRoot
& (Join-Path $acceptance "Invoke-B6-B12-KioskFirstRun.ps1") -RepoRoot $root
& (Join-Path $acceptance "Invoke-B7-KioskUpgrade.ps1") -LowKioskSetup $kioskLow -HighKioskSetup $kioskHigh
& (Join-Path $acceptance "Invoke-B8-WindowsBillingRestart.ps1") -AccountsFile $accounts
& (Join-Path $acceptance "Invoke-B13-DefaultPinUpgrade.ps1") -ServerSetup $serverHigh -AccountsFile $accounts

Copy-Item -Force "C:\ProgramData\Checkpoint\logs\upgrade.log" (Join-Path $logRoot "upgrade.log") -ErrorAction SilentlyContinue
Write-Host "ci-installer: acceptance done"