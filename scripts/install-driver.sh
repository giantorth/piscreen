#!/usr/bin/env bash
# Build the driver and install it as SimHub shared driver "PiScreen":
#   <SimHub>/SharedDrivers/PiScreen/DisplayDriver.exe (+ dependencies + openh264 DLL)
# Usage: scripts/install-driver.sh [SimHub install dir]
#   default: ~/linux-simracing-utils/pfx/drive_c/Program Files (x86)/SimHub
# SimHub restarts the driver by itself; if it is running, it holds the old exe open, so the
# copy goes to a fresh folder and is swapped in.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SIMHUB="${1:-$HOME/linux-simracing-utils/pfx/drive_c/Program Files (x86)/SimHub}"
[[ -f "$SIMHUB/SimHubWPF.exe" ]] || { echo "No SimHubWPF.exe in $SIMHUB" >&2; exit 1; }

"$ROOT/scripts/fetch-openh264.sh"
dotnet build "$ROOT/windows/PiScreen.Driver/PiScreen.Driver.csproj" -c Release -nologo -v q

DEST="$SIMHUB/SharedDrivers/PiScreen"
mkdir -p "$DEST"
rsync -a --delete --exclude '*.pdb' "$ROOT/windows/PiScreen.Driver/bin/Release/" "$DEST/"
echo "Installed to $DEST"
ls "$DEST" | grep -E 'DisplayDriver|openh264'
