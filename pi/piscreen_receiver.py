#!/usr/bin/env python3
"""PiScreen receiver.

Shows a SimHub dashboard streamed by the PiScreen Windows driver:
  WebRTC (H.264) -> webrtcbin -> rtph264depay -> h264parse -> v4l2h264dec (HW) -> kmssink/fbdevsink
and sends the touchscreen back over the "control" data channel.

Signaling is WHIP-style HTTP:
  GET  /info   -> {"name","width","height","touch","version"}  (display size = render size SimHub uses)
  POST /offer  -> body: SDP offer (application/sdp); reply 201 with the SDP answer
  GET  /stats  -> session and frame counters (debugging)

Python only handles signaling and touch; all media work happens in GStreamer's C threads,
which keeps a single-core Pi Zero W usable.
"""
import argparse
import glob
import json
import logging
import os
import signal
import socket
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import gi

gi.require_version("Gst", "1.0")
gi.require_version("GstSdp", "1.0")
gi.require_version("GstWebRTC", "1.0")
gi.require_version("GstVideo", "1.0")
from gi.repository import GLib, Gst, GstSdp, GstVideo, GstWebRTC  # noqa: E402

VERSION = "0.1.0"
log = logging.getLogger("piscreen")


# --------------------------------------------------------------------------- display / backlight

def detect_display_size():
    """Preferred mode of the first connected DRM connector, else the framebuffer size."""
    candidates = []
    for status_path in sorted(glob.glob("/sys/class/drm/card*-*/status")):
        try:
            status = open(status_path).read().strip()
            modes = open(os.path.join(os.path.dirname(status_path), "modes")).read().split()
        except OSError:
            continue
        if modes and status in ("connected", "unknown"):  # SPI/DPI panels can report "unknown"
            candidates.append((status != "connected", status_path, modes[0]))
    if candidates:
        candidates.sort()
        _, path, mode = candidates[0]
        w, h = mode.split("x")
        log.info("Display %s mode %s", os.path.basename(os.path.dirname(path)), mode)
        return int(w), int(h.rstrip("ip"))
    for vs in sorted(glob.glob("/sys/class/graphics/fb*/virtual_size")):
        try:
            w, h = open(vs).read().strip().split(",")
            log.info("Framebuffer %s %sx%s", vs, w, h)
            return int(w), int(h)
        except (OSError, ValueError):
            continue
    log.warning("No display found, assuming 800x480")
    return 800, 480


class Backlight:
    def __init__(self, path):
        self.path = None
        if path == "none":
            return
        if path == "auto":
            found = sorted(glob.glob("/sys/class/backlight/*"))
            path = found[0] if found else None
        if path and os.path.exists(os.path.join(path, "brightness")):
            self.path = path
            self.max = int(open(os.path.join(path, "max_brightness")).read().strip() or 1)
            log.info("Backlight %s (max %d)", path, self.max)

    def set_percent(self, pct):
        if not self.path:
            return
        value = max(0, min(self.max, round(self.max * pct / 100.0)))
        try:
            with open(os.path.join(self.path, "brightness"), "w") as f:
                f.write(str(value))
        except OSError as e:
            log.warning("Backlight write failed: %s", e)


# --------------------------------------------------------------------------- touch

class Touch(threading.Thread):
    """Reads an evdev touchscreen and reports active contacts, normalized 0..1, to `send`."""

    def __init__(self, device, swap_xy, invert_x, invert_y, calibration, send):
        super().__init__(daemon=True, name="touch")
        self.send = send
        self.swap_xy, self.invert_x, self.invert_y = swap_xy, invert_x, invert_y
        self.calibration = calibration
        self.dev = None
        try:
            import evdev  # noqa: F401
        except ImportError:
            log.warning("python3-evdev not installed: touch disabled")
            return
        self.dev = self._open(device)

    @property
    def available(self):
        return self.dev is not None

    def _open(self, device):
        import evdev
        from evdev import ecodes as e
        if device == "none":
            return None
        paths = [device] if device != "auto" else evdev.list_devices()
        for p in paths:
            try:
                d = evdev.InputDevice(p)
            except OSError:
                continue
            caps = d.capabilities()
            abs_codes = [c[0] if isinstance(c, tuple) else c for c in caps.get(e.EV_ABS, [])]
            keys = caps.get(e.EV_KEY, [])
            if e.ABS_MT_POSITION_X in abs_codes or (e.ABS_X in abs_codes and e.BTN_TOUCH in keys):
                log.info("Touch device %s (%s)", d.path, d.name)
                return d
        log.info("No touchscreen found")
        return None

    def run(self):
        if not self.dev:
            return
        from evdev import ecodes as e
        d = self.dev
        mt = e.ABS_MT_POSITION_X in [c[0] if isinstance(c, tuple) else c for c in d.capabilities().get(e.EV_ABS, [])]
        ax = d.absinfo(e.ABS_MT_POSITION_X if mt else e.ABS_X)
        ay = d.absinfo(e.ABS_MT_POSITION_Y if mt else e.ABS_Y)
        xmin, xmax, ymin, ymax = self.calibration or (ax.min, ax.max, ay.min, ay.max)
        slots = {}           # slot -> [tracking_id, x, y]
        slot = 0
        single = [False, 0, 0]
        last_sent = None
        last_time = 0.0

        def norm(x, y):
            nx = (x - xmin) / float(max(1, xmax - xmin))
            ny = (y - ymin) / float(max(1, ymax - ymin))
            if self.swap_xy:
                nx, ny = ny, nx
            if self.invert_x:
                nx = 1.0 - nx
            if self.invert_y:
                ny = 1.0 - ny
            return {"x": round(min(1.0, max(0.0, nx)), 4), "y": round(min(1.0, max(0.0, ny)), 4)}

        for ev in d.read_loop():
            if ev.type == e.EV_ABS:
                if mt:
                    if ev.code == e.ABS_MT_SLOT:
                        slot = ev.value
                    elif ev.code == e.ABS_MT_TRACKING_ID:
                        if ev.value < 0:
                            slots.pop(slot, None)
                        else:
                            slots.setdefault(slot, [ev.value, 0, 0])[0] = ev.value
                    elif ev.code == e.ABS_MT_POSITION_X:
                        slots.setdefault(slot, [0, 0, 0])[1] = ev.value
                    elif ev.code == e.ABS_MT_POSITION_Y:
                        slots.setdefault(slot, [0, 0, 0])[2] = ev.value
                else:
                    if ev.code == e.ABS_X:
                        single[1] = ev.value
                    elif ev.code == e.ABS_Y:
                        single[2] = ev.value
            elif ev.type == e.EV_KEY and ev.code == e.BTN_TOUCH and not mt:
                single[0] = ev.value != 0
            elif ev.type == e.EV_SYN and ev.code == e.SYN_REPORT:
                if mt:
                    points = [norm(v[1], v[2]) for _, v in sorted(slots.items())][:4]
                else:
                    points = [norm(single[1], single[2])] if single[0] else []
                now = time.monotonic()
                # Releases and new contacts go out immediately; moves are capped at ~60/s.
                if points != last_sent and (len(points) != len(last_sent or []) or now - last_time >= 1 / 60):
                    self.send(json.dumps({"type": "touch", "points": points}))
                    last_sent, last_time = points, now


# --------------------------------------------------------------------------- media session

def has_element(name):
    return Gst.ElementFactory.find(name) is not None


def make(factory, **props):
    el = Gst.ElementFactory.make(factory, None)
    if el is None:
        raise RuntimeError("GStreamer element '%s' missing" % factory)
    for k, v in props.items():
        if el.find_property(k.replace("_", "-")) is not None:
            el.set_property(k.replace("_", "-"), v)
    return el


class Session:
    """One webrtcbin pipeline for one driver connection."""

    def __init__(self, opts, on_message, on_closed):
        self.opts = opts
        self.on_message = on_message
        self.on_closed = on_closed
        self.channel = None
        self.frames = 0
        self.started = time.monotonic()
        self.closed = False

        self.pipe = Gst.Pipeline.new("piscreen")
        self.webrtc = make("webrtcbin", bundle_policy=GstWebRTC.WebRTCBundlePolicy.MAX_BUNDLE,
                           latency=opts.latency)
        self.pipe.add(self.webrtc)
        self.webrtc.connect("pad-added", self._on_pad_added)
        self.webrtc.connect("on-data-channel", self._on_data_channel)
        self.webrtc.connect("notify::connection-state", self._on_connection_state)
        bus = self.pipe.get_bus()
        bus.add_signal_watch()
        bus.connect("message::error", self._on_error)
        bus.connect("message::warning", lambda b, m: log.warning("GStreamer: %s", m.parse_warning()[0].message))
        self.pipe.set_state(Gst.State.PLAYING)

    # -- negotiation (called from an HTTP worker thread; webrtcbin promises are thread-safe) --
    def answer(self, offer_sdp, timeout=10.0):
        log.debug("Offer SDP:\n%s", offer_sdp)
        ok, msg = GstSdp.SDPMessage.new_from_text(offer_sdp)
        if ok != GstSdp.SDPResult.OK:
            raise ValueError("unparsable SDP offer")
        offer = GstWebRTC.WebRTCSessionDescription.new(GstWebRTC.WebRTCSDPType.OFFER, msg)
        p = Gst.Promise.new()
        self.webrtc.emit("set-remote-description", offer, p)
        p.wait()
        p = Gst.Promise.new()
        self.webrtc.emit("create-answer", None, p)
        p.wait()
        reply = p.get_reply()
        answer = reply.get_value("answer") if reply else None
        if answer is None:
            raise RuntimeError("webrtcbin produced no answer: %s" % (reply.to_string() if reply else "no reply"))
        p = Gst.Promise.new()
        self.webrtc.emit("set-local-description", answer, p)
        p.wait()
        # No trickle ICE: wait until host candidates are in the local description.
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self.webrtc.get_property("ice-gathering-state") == GstWebRTC.WebRTCICEGatheringState.COMPLETE:
                break
            time.sleep(0.02)
        else:
            log.warning("ICE gathering did not complete in %.0fs; answering with what we have", timeout)
        answer_sdp = self.webrtc.get_property("local-description").sdp.as_text()
        log.debug("Answer SDP:\n%s", answer_sdp)
        return answer_sdp

    # -- media --
    def _on_pad_added(self, webrtc, pad):
        if pad.get_direction() != Gst.PadDirection.SRC:
            return
        caps = pad.get_current_caps()
        s = caps.get_structure(0) if caps else None
        if not s or s.get_string("media") != "video":
            log.info("Ignoring pad %s", caps.to_string() if caps else "?")
            return
        log.info("Incoming %s", s.get_string("encoding-name"))
        elements = [make("queue", max_size_buffers=3, leaky=2),
                    make("rtph264depay", request_keyframe=True, wait_for_keyframe=True),
                    make("h264parse"),
                    self._decoder()]
        elements += self._sink_chain()
        for el in elements:
            self.pipe.add(el)
        for a, b in zip(elements, elements[1:]):
            if not a.link(b):
                log.error("Cannot link %s -> %s", a.get_name(), b.get_name())
        for el in elements:
            el.sync_state_with_parent()
        if isinstance(elements[-1], Gst.Bin):  # autovideosink: say which real sink it picked
            GLib.timeout_add(1000, self._log_bin_children, elements[-1])
        self.depay = elements[1]
        # rtph264depay asks upstream for a keyframe when it sees loss; webrtcbin doesn't reliably
        # turn that into an RTCP PLI, so relay every such request over the data channel too.
        self.depay.get_static_pad("sink").add_probe(Gst.PadProbeType.EVENT_UPSTREAM, self._on_upstream_event)
        self._last_kf_request = 0.0
        decoder = elements[3]
        decoder.get_static_pad("src").add_probe(Gst.PadProbeType.BUFFER, self._count_frame)
        pad.link(elements[0].get_static_pad("sink"))
        # The IDR that triggered pad-added usually arrives before this chain is linked and is
        # dropped; ask for another over the data channel until something decodes (RTCP PLI is
        # the normal path, this covers drivers/SDPs where it isn't negotiated).
        self._kicks = 0
        GLib.timeout_add(300, self._kick_keyframe)
        if self.sink_kind == "desktop":
            # Desktop testing: clicks/drags in the video window act as a single touch.
            self._mouse_down = False
            self._last_move = 0.0
            elements[-2].get_static_pad("src").add_probe(Gst.PadProbeType.EVENT_UPSTREAM, self._on_navigation)

    def _kick_keyframe(self):
        if self.closed or self.frames > 0:
            return False
        self._kicks += 1
        log.info("No picture yet, requesting a keyframe (%d)", self._kicks)
        self.send(json.dumps({"type": "keyframe"}))
        return self._kicks < 20

    @staticmethod
    def _log_bin_children(bin_):
        names, it = [], bin_.iterate_recurse()
        while True:
            res, el = it.next()
            if res != Gst.IteratorResult.OK:
                break
            if el.get_factory():
                names.append(el.get_factory().get_name())
        log.info("Video sink: %s", ", ".join(names))
        return False

    def _on_navigation(self, pad, info):
        ev = info.get_event()
        if ev is None or ev.type != Gst.EventType.NAVIGATION:
            return Gst.PadProbeReturn.OK
        caps = pad.get_current_caps()
        if not caps:
            return Gst.PadProbeReturn.OK
        st = caps.get_structure(0)
        w, h = st.get_value("width"), st.get_value("height")
        kind = GstVideo.Navigation.event_get_type(ev)
        N = GstVideo.NavigationEventType
        if kind in (N.MOUSE_BUTTON_PRESS, N.MOUSE_BUTTON_RELEASE):
            ok, button, x, y = GstVideo.Navigation.event_parse_mouse_button_event(ev)
            if not ok or button != 1:
                return Gst.PadProbeReturn.OK
            self._mouse_down = kind == N.MOUSE_BUTTON_PRESS
        elif kind == N.MOUSE_MOVE and self._mouse_down:
            ok, x, y = GstVideo.Navigation.event_parse_mouse_move_event(ev)
            now = time.monotonic()
            if not ok or now - self._last_move < 1 / 60:
                return Gst.PadProbeReturn.OK
            self._last_move = now
        else:
            return Gst.PadProbeReturn.OK
        points = [{"x": round(min(1, max(0, x / w)), 4), "y": round(min(1, max(0, y / h)), 4)}] if self._mouse_down else []
        if kind != N.MOUSE_MOVE:
            log.info("Click %s at (%.1f, %.1f) on %dx%d video -> %s",
                     "press" if self._mouse_down else "release", x, y, w, h, points)
        self.send(json.dumps({"type": "touch", "points": points}))
        return Gst.PadProbeReturn.OK

    def _on_upstream_event(self, pad, info):
        ev = info.get_event()
        if ev is not None and GstVideo.video_event_is_force_key_unit(ev):
            now = time.monotonic()
            if now - self._last_kf_request > 0.25:  # one request per 250 ms is plenty
                self._last_kf_request = now
                log.info("Decoder needs a keyframe (packet loss?), asking the driver")
                self.send(json.dumps({"type": "keyframe"}))
        return Gst.PadProbeReturn.OK

    def request_keyframe_rtcp(self):
        """Ask upstream for a keyframe the way packet loss does: rtpsession turns it into an RTCP PLI."""
        depay = getattr(self, "depay", None)
        if depay is None:
            return False
        ev = GstVideo.video_event_new_upstream_force_key_unit(Gst.CLOCK_TIME_NONE, True, 0)
        return depay.get_static_pad("sink").push_event(ev)

    def snapshot_png(self, timeout=3.0):
        """PNG of the next decoded frame. Copies one frame on request only: holding decoder
        buffers (dmabuf pool on the Pi) would stall the hardware decoder."""
        self._snap_event = threading.Event()
        self._snap_sample = None
        self._want_snapshot = True
        if not self._snap_event.wait(timeout):
            self._want_snapshot = False
            return None
        png = GstVideo.video_convert_sample(self._snap_sample, Gst.Caps.from_string("image/png"), Gst.SECOND)
        ok, info = png.get_buffer().map(Gst.MapFlags.READ)
        data = bytes(info.data)
        png.get_buffer().unmap(info)
        return data

    def _count_frame(self, pad, info):
        if getattr(self, "_want_snapshot", False):
            self._want_snapshot = False
            self._snap_sample = Gst.Sample.new(info.get_buffer().copy_deep(), pad.get_current_caps(), None, None)
            self._snap_event.set()
        self.frames += 1
        if self.frames == 1:
            log.info("First frame decoded (%s)", pad.get_current_caps().to_string() if pad.get_current_caps() else "?")
        return Gst.PadProbeReturn.OK

    def _decoder(self):
        choice = self.opts.decoder
        if choice == "auto":
            for name in ("v4l2h264dec", "avdec_h264", "openh264dec"):
                if has_element(name):
                    choice = name
                    break
        log.info("Decoder %s", choice)
        return make(choice)

    def _sink_chain(self):
        sink = self.opts.sink
        if sink == "auto":
            if os.environ.get("WAYLAND_DISPLAY") or os.environ.get("DISPLAY"):
                sink = "desktop"
            elif glob.glob("/dev/dri/card*"):
                sink = "kms"
            else:
                sink = "fbdev"
        self.sink_kind = sink
        log.info("Sink %s", sink)
        if sink == "kms":
            kms = make("kmssink", sync=False)
            if self.opts.kms_driver:
                kms.set_property("driver-name", self.opts.kms_driver)
            if self.opts.kms_connector >= 0:
                kms.set_property("connector-id", self.opts.kms_connector)
            return [make("videoconvert"), kms]
        if sink == "fbdev":
            return [make("videoconvert"), make("videoscale"),
                    make("fbdevsink", device=self.opts.fbdev, sync=False)]
        if sink == "desktop":
            return [make("videoconvert"), make("autovideosink", sync=False)]
        if sink == "fake":
            return [make("fakesink", sync=False)]
        if sink.startswith("png:"):  # debugging: keep the last few decoded frames as PNGs
            return [make("videoconvert"), make("pngenc"),
                    make("multifilesink", location=sink[4:], max_files=5)]
        raise ValueError("unknown sink " + sink)

    # -- data channel --
    def _on_data_channel(self, webrtc, channel):
        log.info("Data channel '%s'", channel.get_property("label"))
        self.channel = channel
        channel.connect("on-message-string", lambda ch, msg: self.on_message(msg))
        channel.connect("on-open", lambda ch: log.info("Data channel open"))

    def send(self, text):
        ch = self.channel
        if ch is None or ch.get_property("ready-state") != GstWebRTC.WebRTCDataChannelState.OPEN:
            return
        try:
            if hasattr(ch, "send_string_full"):
                ch.send_string_full(text)
            else:
                ch.emit("send-string", text)
        except GLib.Error as e:
            log.warning("Data channel send failed: %s", e)

    # -- lifecycle --
    def _on_connection_state(self, webrtc, _pspec):
        state = webrtc.get_property("connection-state")
        log.info("WebRTC %s", state.value_nick)
        if state in (GstWebRTC.WebRTCPeerConnectionState.FAILED, GstWebRTC.WebRTCPeerConnectionState.CLOSED):
            GLib.idle_add(self.on_closed, self)

    def _on_error(self, bus, msg):
        err, dbg = msg.parse_error()
        log.error("GStreamer error from %s: %s (%s)", msg.src.get_name(), err.message, dbg)
        GLib.idle_add(self.on_closed, self)

    def stop(self):
        if self.closed:
            return
        self.closed = True
        self.pipe.get_bus().remove_signal_watch()
        self.pipe.set_state(Gst.State.NULL)


# --------------------------------------------------------------------------- service

class Receiver:
    def __init__(self, opts):
        self.opts = opts
        self.lock = threading.Lock()
        self.session = None
        if opts.width and opts.height:
            self.width, self.height = opts.width, opts.height
        else:
            self.width, self.height = detect_display_size()
        self.backlight = Backlight(opts.backlight)
        self.brightness = 100
        self.simhub = False
        calib = tuple(int(v) for v in opts.touch_calibration.split(",")) if opts.touch_calibration else None
        self.touch = Touch(opts.touch_device, opts.touch_swap_xy, opts.touch_invert_x, opts.touch_invert_y,
                           calib, self.send)
        self.touch.start()

    def info(self):
        return {"name": socket.gethostname(), "width": self.width, "height": self.height,
                "touch": self.touch.available, "version": VERSION}

    def stats(self):
        s = self.session
        return {"session": s is not None and not s.closed,
                "frames": s.frames if s else 0,
                "uptime_s": round(time.monotonic() - s.started, 1) if s else 0,
                "simhub": self.simhub, "brightness": self.brightness}

    def new_session(self, offer_sdp):
        with self.lock:
            if self.session:
                log.info("Replacing existing session")
                self.session.stop()
            self.session = Session(self.opts, self.on_message, self.on_session_closed)
            return self.session.answer(offer_sdp)

    def on_session_closed(self, session):
        with self.lock:
            if self.session is session:
                log.info("Session ended after %d frames", session.frames)
                session.stop()
                self.session = None
        return False  # one-shot idle callback

    def send(self, text):
        s = self.session
        if s:
            s.send(text)

    def on_message(self, text):
        try:
            msg = json.loads(text)
        except ValueError:
            log.warning("Bad control message: %r", text)
            return
        kind = msg.get("type")
        if kind == "brightness":
            self.brightness = int(msg.get("value", 100))
            if self.simhub or not self.opts.idle_backlight_off:
                self.backlight.set_percent(self.brightness)
        elif kind == "state":
            self.simhub = bool(msg.get("simhub"))
            log.info("SimHub %s", "rendering" if self.simhub else "idle")
            if self.opts.idle_backlight_off:
                self.backlight.set_percent(self.brightness if self.simhub else 0)
        else:
            log.info("Control message: %s", text)


def make_handler(rx):
    class Handler(BaseHTTPRequestHandler):
        server_version = "piscreen/" + VERSION

        def log_message(self, fmt, *args):
            log.debug("%s %s", self.address_string(), fmt % args)

        def _reply(self, code, body, ctype):
            data = body.encode() if isinstance(body, str) else body
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            if self.path == "/info":
                self._reply(200, json.dumps(rx.info()), "application/json")
            elif self.path == "/snapshot":  # next decoded frame as PNG (waits for SimHub to render one)
                s = rx.session
                png = s.snapshot_png() if s else None
                if png:
                    self._reply(200, png, "image/png")
                else:
                    self._reply(409, "no frame within 3 s (no session, or SimHub idle)\n", "text/plain")
            elif self.path == "/stats":
                self._reply(200, json.dumps(rx.stats()), "application/json")
            else:
                self._reply(404, "not found\n", "text/plain")

        def do_POST(self):
            if self.path == "/touch":  # testing without a touchscreen: body = {"points":[{"x":0.5,"y":0.5}]}
                length = int(self.headers.get("Content-Length") or 0)
                points = json.loads(self.rfile.read(length) or b"{}").get("points", [])
                rx.send(json.dumps({"type": "touch", "points": points}))
                self._reply(204, b"", "text/plain")
                return
            if self.path == "/pli":  # testing: request a keyframe the way the depayloader does on packet loss
                s = rx.session
                ok = bool(s and s.request_keyframe_rtcp())
                self._reply(200 if ok else 409, ("sent\n" if ok else "no video session\n"), "text/plain")
                return
            if self.path != "/offer":
                self._reply(404, "not found\n", "text/plain")
                return
            length = int(self.headers.get("Content-Length") or 0)
            if length <= 0 or length > 64 * 1024:
                self._reply(400, "missing or oversized SDP\n", "text/plain")
                return
            offer = self.rfile.read(length).decode("utf-8", "replace")
            log.info("Offer from %s", self.client_address[0])
            try:
                answer = rx.new_session(offer)
            except Exception as e:  # report negotiation failures to the driver
                log.exception("Negotiation failed")
                self._reply(500, "negotiation failed: %s\n" % e, "text/plain")
                return
            self._reply(201, answer, "application/sdp")

    return Handler


def parse_args():
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    p.add_argument("--config", default="/etc/piscreen.conf",
                   help="key=value file; keys are the long option names without dashes")
    p.add_argument("--bind", default="0.0.0.0")
    p.add_argument("--port", type=int, default=8090)
    p.add_argument("--width", type=int, default=0, help="override detected display width")
    p.add_argument("--height", type=int, default=0, help="override detected display height")
    p.add_argument("--decoder", default="auto", help="auto | v4l2h264dec | avdec_h264 | openh264dec")
    p.add_argument("--sink", default="auto", help="auto | kms | fbdev | desktop | fake | png:/path/frame-%%05d.png")
    p.add_argument("--kms-driver", default="", help="kmssink driver-name, e.g. vc4 (default: kmssink picks)")
    p.add_argument("--kms-connector", type=int, default=-1, help="kmssink connector-id")
    p.add_argument("--fbdev", default="/dev/fb0")
    p.add_argument("--latency", type=int, default=20, help="jitter buffer, ms")
    p.add_argument("--touch-device", default="auto", help="auto | none | /dev/input/eventN")
    p.add_argument("--touch-swap-xy", action="store_true")
    p.add_argument("--touch-invert-x", action="store_true")
    p.add_argument("--touch-invert-y", action="store_true")
    p.add_argument("--touch-calibration", default="", help="xmin,xmax,ymin,ymax raw touch range")
    p.add_argument("--backlight", default="auto", help="auto | none | /sys/class/backlight/<name>")
    p.add_argument("--idle-backlight-off", action="store_true", help="turn the backlight off while SimHub is idle")
    p.add_argument("-v", "--verbose", action="store_true")

    # Config file values become defaults; command-line flags still win.
    pre, _ = p.parse_known_args()
    if os.path.exists(pre.config):
        defaults = {}
        for line in open(pre.config):
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            k, v = (x.strip() for x in line.split("=", 1))
            v = v.split(" #", 1)[0].strip()  # allow "key = value  # comment"
            k = k.replace("-", "_")
            action = next((a for a in p._actions if a.dest == k), None)
            if action is None:
                continue
            if action.const is True:  # store_true flags
                defaults[k] = v.lower() in ("1", "true", "yes", "on")
            elif action.type is int:
                defaults[k] = int(v)
            else:
                defaults[k] = v
        p.set_defaults(**defaults)
    return p.parse_args()


def main():
    opts = parse_args()
    logging.basicConfig(level=logging.DEBUG if opts.verbose else logging.INFO,
                        format="%(asctime)s %(levelname)s %(message)s")
    Gst.init(None)
    log.info("PiScreen receiver %s, GStreamer %s", VERSION, Gst.version_string())
    for el in ("webrtcbin", "rtph264depay", "h264parse", "nicesrc", "sctpenc"):
        if not has_element(el):
            log.error("Missing GStreamer element %s (see pi/install.sh)", el)

    rx = Receiver(opts)
    log.info("Display %dx%d, touch %s", rx.width, rx.height, "yes" if rx.touch.available else "no")
    server = ThreadingHTTPServer((opts.bind, opts.port), make_handler(rx))
    server.daemon_threads = True
    threading.Thread(target=server.serve_forever, daemon=True, name="http").start()
    log.info("Listening on http://%s:%d", opts.bind, opts.port)

    loop = GLib.MainLoop()
    try:  # PyGObject >= 3.52 moved this to GLibUnix; Bookworm's PyGObject only has the old name
        gi.require_version("GLibUnix", "2.0")
        from gi.repository import GLibUnix
        add_signal = GLibUnix.signal_add
    except (ImportError, ValueError):
        add_signal = GLib.unix_signal_add
    for sig in (signal.SIGINT, signal.SIGTERM):
        add_signal(GLib.PRIORITY_DEFAULT, sig, lambda *a: (loop.quit(), False)[1])
    try:
        loop.run()
    finally:
        server.shutdown()
        if rx.session:
            rx.session.stop()


if __name__ == "__main__":
    main()
