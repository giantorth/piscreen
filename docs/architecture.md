# PiScreen architecture

## Data path

```
SimHub (WPF dash render)
  │  BGRA frames, double-buffered, in MMF "SimHubDashRenderV3<family>_<id1>_<id2>"
  ▼
DisplayDriver.exe  (net48 x64, launched by SimHub)
  ScreenChannel.Poll        copy last filled buffer, drop torn reads (UpdateCount changed)
  ColorConvert.BgraToI420   BT.601 limited range, 2x2 chroma average (~0.4 ms @ 800x480)
  OpenH264Encoder           Constrained Baseline, SCREEN_CONTENT_REAL_TIME, RC bitrate mode,
                            no frame skipping, IDR on connect / request / every N s (~1 ms)
  WebRtcLink (SIPSorcery)   H.264 RTP (FU-A), DTLS-SRTP, "control" SCTP data channel
  ▼  LAN, host ICE candidates only
piscreen_receiver.py (Pi)
  webrtcbin → queue → rtph264depay → h264parse → v4l2h264dec → videoconvert → kmssink
                                                               (fbdevsink / autovideosink / fakesink)
```

Frames are irregular. SimHub writes only when the dashboard changes, plus a keep-alive about once a
second. The driver encodes each new frame, capped at `max_fps`, and advances the RTP clock by the
real time between frames. The Pi's sinks run with `sync=false`, so each frame shows as soon as it's
decoded.

## SimHub side: MMF V3 screen driver contract

Found by decompiling SimHub 9.13.7 (`SimHub.Plugins.dll`, `SimHub.BitmapDisplay.MMF.dll`) and the
sample driver it ships in `SharedDrivers\VirtualExternalDisplayDriverSampleCSharp`.

- **Device Builder → Screen → "MMF Rendering (External process consumer)"** (`MMFScreen`). The
  family id is the device definition's `DescriptorUniqueId` GUID.
- **Driver lookup:** "Shared" means `<SimHub>\SharedDrivers\<name>\DisplayDriver.exe`; "Embedded"
  means `<definition folder>\LocalDriver\<name>\DisplayDriver.exe`.
- **Launch:** `DisplayDriver.exe -knowndevices <definitionId>,SimHubDashIndexV3<guid>[,<modelKey>];…`.
  It's started through `ExternalProcessHost` with `restartOnExit`. SimHub keeps stdin as a pipe and
  closes it to ask the driver to exit, then kills it after `ShutdownTimeoutSeconds`.
- **Detection:** `-detect` plus the same arguments. SimHub holds the index MMF open, gives the run
  5 s, then reads the entries, including ones marked not available. The driver must not use the
  network here and must not clear the index on exit.
- **Index MMF** `SimHubDashIndexV3<guid>`: 10 × `DeviceEntry` (`DeviceID[64]`, two random ids,
  `DeviceInfo[256]`, `Available`). SimHub attaches device instances to `DeviceID`, so it has to stay
  stable across runs (`piscreen-1`). `DeviceInfo` becomes the model name SimHub shows.
- **Render MMF** `SimHubDashRenderV3<guid>_<id1>_<id2>`, struct `MMFHeaderV3` (16,603,161 bytes;
  see `Mmf/MmfStructs.cs`):
  - Driver writes: `StructVersion=3`, requested size, `RequestDoubleBuffer`, `ReadTimeout`,
    `Capabilities` (bit 0 = has a configuration UI), then `RequestActive=true`. Setting the
    `Global\SHMMFNotification` event makes SimHub connect now instead of on its 5 s poll.
  - Driver must increment `ReadCount` continually. SimHub disconnects after `ReadTimeout` seconds
    without a change.
  - SimHub's draw: `DisplayBufferCurrentlyWritten=n`, `UpdateCount++` on buffer n, copy, then
    `DisplayWriteCount++`, `DisplayBufferCurrentlyWritten=0`, `DisplayLastFilledBuffer=n`. A reader
    copies `LastFilledBuffer` when it isn't the one being written. If `UpdateCount` changed during
    the copy, the read is torn.
  - Touch: `Cursor1..4 {bool pressed, int x, int y}` in render pixels. SimHub before 9.11.18 only
    reads `Cursor4`, so the first contact goes there. SimHub reads touch once per render-loop pass;
    in testing, 30 ms taps were never missed.
  - Touch mode, in order: `RequestedTouchMode` (1 = simplified, 2 = advanced/buttons; read on
    connect), then the dashboard's `Metadata.PreferredTouchMode` if the device's touch mode is auto,
    then the device's `TouchMode` setting. Simplified mode uses the first active point: left half =
    back, right half = forward, with a 300 ms debounce. Advanced passes all points to the renderer,
    which presses `ButtonItem`s. `EnableTouchTraces` makes SimHub draw a 10×10 marker with its
    top-left corner at each touch point. Testing confirmed the mapping is exact.
  - "Configure" button: SimHub sets `OwnerWindowHandle` and increments `ConfigurationRequestCount`.
  - `SimHubConnected` stays true if SimHub dies; use the write counters for liveness.

## Signaling (HTTP on the Pi, port 8090)

| Request | Response |
|---|---|
| `GET /info` | `{"name","width","height","touch","version"}`: display size = render size |
| `POST /offer` (`application/sdp`) | `201` + answer SDP. Replaces any existing session. No trickle ICE: both SDPs are complete. |
| `GET /stats` | session flag, decoded frame count, SimHub state, brightness |
| `POST /touch` `{"points":[{"x","y"}]}` | debug: inject a touch as if from the panel |
| `POST /pli` | debug: request a keyframe the way the depayloader does on packet loss |
| `GET /snapshot` | debug: next decoded frame as PNG (copies one frame on request; never holds decoder buffers) |

The driver is always the offerer. It adds `a=rtcp-fb:96 nack pli` and `ccm fir` to SIPSorcery's
offer, which only advertises `transport-cc`. Plain `nack` is left out because SIPSorcery doesn't
retransmit.

## Control data channel (`"control"`, ordered, JSON text)

| Direction | Message |
|---|---|
| Pi → driver | `{"type":"touch","points":[{"x":0..1,"y":0..1},…]}`: all active contacts (max 4), normalized to the video frame; `[]` = released |
| Pi → driver | `{"type":"keyframe"}` |
| driver → Pi | `{"type":"state","simhub":bool,"width":w,"height":h}` |
| driver → Pi | `{"type":"brightness","value":0..100}`: SimHub's screen brightness, applied to `/sys/class/backlight` |

**Keyframes.** The first IDR usually arrives before the decode chain is linked to webrtcbin's new
pad, so it gets dropped. Separately, webrtcbin didn't turn `rtph264depay`'s force-key-unit requests
into an RTCP PLI in testing (GStreamer 1.28). So the receiver relays every force-key-unit event it
sees on the depayloader's sink pad as a `keyframe` message. It also re-requests a keyframe every
300 ms until the first frame decodes. The driver also sends an IDR every `keyframe_interval_s`
(default 5 s) and whenever SimHub starts rendering.

## Choices and why

- **External driver, not a SimHub plugin:** SimHub owns its lifecycle, it always runs in SimHub's
  Wine prefix, and a crash can't take SimHub down.
- **net48:** SimHub's own runtime, so it's present on every Windows SimHub machine and in its Wine
  prefix.
- **OpenH264 via direct P/Invoke, not a wrapper:** Cisco's DLL imports only `KERNEL32`, so it needs no
  VC++ runtime on Windows and runs under Wine. `SLayerBSInfo` gained `float rPsnr[3]` in 2.6.0, so
  the driver reads the library version and picks the matching `SFrameBSInfo` stride.
- **OpenH264 fetched at first run, not shipped:** Cisco's patent coverage applies to copies
  downloaded from Cisco. The driver downloads 2.6.0 over HTTPS (HTTP fallback), verifies a pinned
  SHA-256, and caches it in `%LOCALAPPDATA%\PiScreen`.
- **Constrained Baseline:** what OpenH264 produces and what the Pi's VideoCore IV decoder and every
  WebRTC stack accept.
- **Python on the Pi:** it only does signaling and touch. All media runs in GStreamer's C threads,
  which matters on a single-core Zero W.

## Known limitations / next steps

- No real Pi hardware tested yet. Watch for: `v4l2h264dec` + `kmssink` negotiation on the
  specific panel, the CPU load of DTLS/SRTP on the ARMv6 Zero W, and Wi-Fi loss behaviour.
- One screen per driver: a second Device Builder definition using the driver is ignored, with a
  warning.
- No authentication on the Pi's HTTP endpoint (LAN only).
- No idle screen on the Pi when SimHub stops rendering: the last frame stays up. `idle_backlight_off`
  turns the backlight off instead, if the panel has one.
- The driver's `-detect` lists `piscreen-1` without checking the Pi is reachable (SimHub's 5 s budget).
