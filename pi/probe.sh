#!/usr/bin/env bash
# PiScreen hardware probe: what display, touch controller and H.264 decoder this Pi has, and
# whether the GStreamer pieces the receiver needs are installed. Read-only unless --display-test.
#   ./probe.sh                  report only
#   sudo ./probe.sh --display-test   also shows colour bars on the panel for 5 s (stop piscreen first)
section() { printf '\n== %s\n' "$1"; }

section "Board / OS"
tr -d '\0' < /proc/device-tree/model 2>/dev/null; echo
. /etc/os-release 2>/dev/null && echo "$PRETTY_NAME, $(uname -m), kernel $(uname -r)"

section "Display overlays (config.txt)"
grep -h -E '^\s*(dtoverlay|dtparam|display_|hdmi_|lcd_|max_framebuffers|disable_fw_kms_setup)' \
  /boot/firmware/config.txt /boot/config.txt 2>/dev/null || echo "(none found)"

section "DRM connectors"
for c in /sys/class/drm/card*-*; do
  [[ -e "$c/status" ]] && echo "$(basename "$c"): $(cat "$c/status")  modes: $(tr '\n' ' ' < "$c/modes")"
done
for d in /sys/class/drm/card?; do
  [[ -e "$d/device/driver" ]] && echo "$(basename "$d") driver: $(basename "$(readlink -f "$d/device/driver")")"
done

section "Framebuffers"
for f in /sys/class/graphics/fb*; do
  [[ -e "$f" ]] && echo "$(basename "$f"): $(cat "$f/name" 2>/dev/null) $(cat "$f/virtual_size" 2>/dev/null) bpp=$(cat "$f/bits_per_pixel" 2>/dev/null)"
done

section "V4L2 devices (H.264 decoder should be bcm2835-codec-decode)"
for v in /sys/class/video4linux/video*; do [[ -e "$v/name" ]] && echo "$(basename "$v"): $(cat "$v/name")"; done

section "Input devices with absolute axes (touchscreens)"
awk '/^N:/{name=$0} /^H:/{h=$0} /^B: ABS/{print name; print "   " h; print "   " $0}' /proc/bus/input/devices

section "Backlight"
ls /sys/class/backlight 2>/dev/null || echo "(none)"

section "GStreamer elements"
gst-inspect-1.0 --version 2>/dev/null | head -1 || echo "gstreamer not installed"
for e in webrtcbin nicesrc dtlssrtpdec sctpenc rtph264depay h264parse v4l2h264dec avdec_h264 kmssink fbdevsink; do
  printf '  %-14s %s\n' "$e" "$(gst-inspect-1.0 "$e" >/dev/null 2>&1 && echo ok || echo MISSING)"
done

section "Python bindings"
python3 - <<'PY'
import gi
for ns in ("Gst", "GstSdp", "GstWebRTC", "GstVideo"):
    try:
        gi.require_version(ns, "1.0"); __import__("gi.repository." + ns); print("  %-10s ok" % ns)
    except Exception as e:
        print("  %-10s MISSING (%s)" % (ns, e))
try:
    import evdev; print("  evdev      ok")
except ImportError:
    print("  evdev      MISSING")
PY

section "Hardware H.264 round trip (encode + decode 120 frames, 800x480)"
if gst-inspect-1.0 v4l2h264enc >/dev/null 2>&1 && gst-inspect-1.0 v4l2h264dec >/dev/null 2>&1; then
  start=$(date +%s%N)
  if timeout 60 gst-launch-1.0 -q videotestsrc num-buffers=120 ! video/x-raw,width=800,height=480,format=I420,framerate=60/1 \
       ! v4l2h264enc ! 'video/x-h264,level=(string)4' ! h264parse ! v4l2h264dec ! fakesink sync=false; then
    echo "  ok in $(( ($(date +%s%N) - start) / 1000000 )) ms"
  else
    echo "  FAILED"
  fi
else
  echo "  skipped (v4l2h264enc/dec not available)"
fi

section "Network"
echo "  hostname: $(hostname)  addresses: $(hostname -I)"
command -v iw >/dev/null && iw dev wlan0 link 2>/dev/null | grep -E 'SSID|signal|tx bitrate' | sed 's/^/  /'

if [[ "${1:-}" == "--display-test" ]]; then
  section "Display test: colour bars for 5 s via kmssink"
  timeout 5 gst-launch-1.0 -q videotestsrc pattern=smpte ! kmssink || \
  timeout 5 gst-launch-1.0 -q videotestsrc pattern=smpte ! videoconvert ! fbdevsink
fi
