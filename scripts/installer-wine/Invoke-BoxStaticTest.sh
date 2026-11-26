#!/bin/bash
# Box (Linux, pwsh 7) runner for installer/tests/*.Tests.ps1 files that need a C: drive and powershell.exe
# (e.g. BackupRestoreConfig.Tests.ps1, the BL-1 low-disk flag check). Maps a PSDrive C: to a temp root in the test
# process AND in every child started as powershell.exe (shim on PATH). Exit code = the test's exit code.
# Usage: Invoke-BoxStaticTest.sh <repo-root> <test-file-relative-to-repo>
set -euo pipefail
REPO=$(cd "$1" && pwd); TEST=$2
T=$(mktemp -d /tmp/cp-box-static.XXXXXX); trap 'rm -rf "$T"' EXIT
mkdir -p "$T/c/ProgramData/Checkpoint/logs" "$T/c/Program Files" "$T/bin"
cat > "$T/launch.ps1" <<PS1
\$ErrorActionPreference = "Stop"
\$null = New-PSDrive -Name C -PSProvider FileSystem -Root '$T/c' -Scope Global
\$mode = \$args[0]
\$file = \$args[1]
\$parts = @(\$args | Select-Object -Skip 2 | ForEach-Object { if ("\$_" -like '-*') { "\$_" } else { "'" + ("\$_" -replace "'", "''") + "'" } })
# top: a test file signals failure by throwing (exit 1), success = 0. child: keep the script's own exit code.
try { Invoke-Expression ("& '" + \$file + "' " + (\$parts -join ' ')); if (\$mode -eq 'top') { exit 0 } else { exit \$LASTEXITCODE } } catch { Write-Host ("SCRIPT-TERMINATING-ERROR: " + \$_.Exception.Message); exit 1 }
PS1
cat > "$T/bin/powershell.exe" <<SH
#!/bin/bash
# powershell.exe shim: drop -NoProfile/-ExecutionPolicy X, take -File <script> rest...
args=(); file=""
while [ \$# -gt 0 ]; do
  case "\$1" in
    -NoProfile|-NonInteractive) shift;;
    -ExecutionPolicy) shift 2;;
    -File) file=\$2; shift 2; args=("\$@"); break;;
    *) echo "powershell.exe shim: unsupported arg \$1" >&2; exit 97;;
  esac
done
exec pwsh -NoProfile -NonInteractive -File "$T/launch.ps1" child "\$file" "\${args[@]}"
SH
chmod +x "$T/bin/powershell.exe"
cd "$T"
PATH="$T/bin:$PATH" pwsh -NoProfile -NonInteractive -File "$T/launch.ps1" top "$REPO/$TEST"
