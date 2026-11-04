#!/usr/bin/env bash
# Runs during Cloud Agent Builds. Must stay idempotent.
# Restores GameClub.slnx when it exists, including the net48 kiosk, so Linux can compile it.
set -euo pipefail
if ! dotnet --version >/dev/null 2>&1; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  if [ -f global.json ]; then
    bash /tmp/dotnet-install.sh --jsonfile global.json --install-dir "$HOME/.dotnet"
  else
    bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
  fi
  sudo ln -sf "$HOME/.dotnet/dotnet" /usr/local/bin/dotnet || true
  export PATH="$HOME/.dotnet:$PATH"
fi
chmod +x scripts/agent/*.sh
if [ -f GameClub.slnx ]; then
  dotnet restore GameClub.slnx --nologo -v q
else
  find . -name '*.csproj' -print0 | xargs -0 -r -n1 dotnet restore --nologo -v q
fi
if [ -f .config/dotnet-tools.json ]; then dotnet tool restore; fi
# When the React PWA exists: (cd web && npm ci)
