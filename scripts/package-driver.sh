#!/usr/bin/env bash
# Builds the release assets into dist/:
#   PiScreen-driver.zip  install.ps1 + PiScreen\ (DisplayDriver.exe and dependencies). No Cisco
#                        OpenH264 DLL: the driver downloads it from Cisco on first run.
#   piscreen-pi.tar.gz   the Pi receiver, installer, service, config and probe.
# Asset names carry no version so README links can use releases/latest/download/<name>.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/windows/PiScreen.Driver/PiScreen.Driver.csproj")"
dotnet build "$ROOT/windows/PiScreen.Driver/PiScreen.Driver.csproj" -c Release -nologo -v q

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$ROOT/dist"

PKG="$STAGE/PiScreen-driver-$VERSION"
mkdir -p "$PKG/PiScreen"
rsync -a --exclude '*.pdb' --exclude 'openh264*' "$ROOT/windows/PiScreen.Driver/bin/Release/" "$PKG/PiScreen/"
cp "$ROOT/windows/package/install.ps1" "$PKG/"
cp "$ROOT/LICENSE" "$PKG/"
rm -f "$ROOT/dist/PiScreen-driver.zip"
(cd "$STAGE" && zip -qr "$ROOT/dist/PiScreen-driver.zip" "PiScreen-driver-$VERSION")

PI="$STAGE/piscreen-pi-$VERSION"
mkdir -p "$PI"
cp "$ROOT/pi/"{piscreen_receiver.py,install.sh,probe.sh,piscreen.service,piscreen.conf} "$ROOT/LICENSE" "$PI/"
tar -C "$STAGE" -czf "$ROOT/dist/piscreen-pi.tar.gz" "piscreen-pi-$VERSION"

echo "Built v$VERSION:"
ls -l "$ROOT/dist/PiScreen-driver.zip" "$ROOT/dist/piscreen-pi.tar.gz"
