#!/usr/bin/env bash
# Downloads Cisco's prebuilt OpenH264 encoder DLL (win64) into windows/third_party/.
# Cisco's binary carries their H.264 patent licence; it is downloaded, never committed.
set -euo pipefail
VERSION="${OPENH264_VERSION:-2.6.0}"
DEST="$(cd "$(dirname "$0")/.." && pwd)/windows/third_party"
NAME="openh264-${VERSION}-win64.dll"
mkdir -p "$DEST"
if [[ -f "$DEST/$NAME" ]]; then echo "$DEST/$NAME already present"; exit 0; fi
curl -fsSL "http://ciscobinary.openh264.org/${NAME}.bz2" -o "$DEST/${NAME}.bz2"
bunzip2 -f "$DEST/${NAME}.bz2"
echo "Fetched $DEST/$NAME"
