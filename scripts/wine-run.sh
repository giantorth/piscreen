#!/usr/bin/env bash
# Run a Windows program inside SimHub's Wine prefix (Linux development/testing).
#   SIMHUB_PREFIX  Wine prefix SimHub lives in   (default: ~/linux-simracing-utils/pfx)
#   SIMHUB_WINE    wine binary that owns it      (default: ~/linux-simracing-utils/bin/wine, else `wine`)
# Must be the same wine build SimHub runs on, or the wineserver refuses the connection.
set -euo pipefail
export WINEPREFIX="${SIMHUB_PREFIX:-$HOME/linux-simracing-utils/pfx}"
WINE="${SIMHUB_WINE:-}"
if [[ -z "$WINE" ]]; then
  if [[ -x "$HOME/linux-simracing-utils/bin/wine" ]]; then WINE="$HOME/linux-simracing-utils/bin/wine"; else WINE=wine; fi
fi
export PATH="$(dirname "$WINE"):$PATH"
export WINEDEBUG="${WINEDEBUG:--all}"
unset DOTNET_ROOT DOTNET_ROOT_X86 DOTNET_ROOT_X64 DOTNET_ROOT_ARM64
exec "$WINE" "$@"
