#!/usr/bin/env bash
# Runs during Cloud Agent Builds. Must stay idempotent.
# Linux only: the WPF kiosk and the net48 guard are excluded on purpose.
# GameClub.Kiosk.Core is restored; Linux tests target it.
set -euo pipefail
if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
  sudo ln -sf "$HOME/.dotnet/dotnet" /usr/local/bin/dotnet
fi
chmod +x scripts/agent/*.sh
find . -name '*.csproj' \
  -not -path './src/GameClub.Kiosk/*' \
  -not -path './src/GameClub.Kiosk.Guard/*' \
  -not -path './kiosk/*' \
  -print0 \
  | xargs -0 -r -n1 dotnet restore --nologo -v q
if [ -f .config/dotnet-tools.json ]; then dotnet tool restore; fi
# When the React PWA exists: (cd web && npm ci)
