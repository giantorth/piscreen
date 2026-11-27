#!/usr/bin/env bash
# Installs the PiScreen receiver as a systemd service on Raspberry Pi OS (Bookworm or newer, Lite is fine).
# Run from this folder on the Pi:  sudo ./install.sh
set -euo pipefail
[[ $EUID -eq 0 ]] || { echo "Run with sudo: sudo ./install.sh" >&2; exit 1; }
cd "$(dirname "$0")"

echo "==> Installing packages (GStreamer WebRTC stack, Python bindings, evdev)"
apt-get update
apt-get install -y --no-install-recommends \
  python3 python3-gi python3-evdev \
  gir1.2-gstreamer-1.0 gir1.2-gst-plugins-base-1.0 gir1.2-gst-plugins-bad-1.0 \
  gstreamer1.0-tools gstreamer1.0-plugins-base gstreamer1.0-plugins-good \
  gstreamer1.0-plugins-bad gstreamer1.0-nice gstreamer1.0-libav

echo "==> Installing /opt/piscreen and the service"
install -d /opt/piscreen
install -m 0755 piscreen_receiver.py probe.sh /opt/piscreen/
if [[ -f /etc/piscreen.conf ]]; then
  echo "    keeping existing /etc/piscreen.conf"
else
  install -m 0644 piscreen.conf /etc/piscreen.conf
fi
install -m 0644 piscreen.service /etc/systemd/system/piscreen.service
systemctl daemon-reload
systemctl enable piscreen.service
systemctl restart piscreen.service

sleep 3
systemctl --no-pager --lines=15 status piscreen.service || true
echo
echo "Receiver installed. Check it with:  curl http://$(hostname).local:8090/info"
echo "Logs:                                journalctl -u piscreen -f"
