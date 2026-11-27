# PiScreen

Show a SimHub dashboard on a Raspberry Pi Zero's screen over the network, with touch.

```
SimHub ──(shared-memory screen)──► DisplayDriver.exe ──(WebRTC H.264 + data channel)──► Pi Zero W ──► panel
   ▲                                  Windows PC                                          touch │
   └──────────────────────────── touch points ◄──────────────────────────────────────────────────┘
```

- **Windows side:** `DisplayDriver.exe`, a SimHub "MMF screen" driver. SimHub starts and stops it.
  It reads frames from SimHub, encodes them with Cisco OpenH264 and sends them over WebRTC. It also
  passes touch back to SimHub.
- **Pi side:** `piscreen_receiver.py`, a systemd service. GStreamer `webrtcbin` receives the stream
  and the Pi's hardware H.264 decoder draws it on the display (KMS, or fbdev as a fallback). Touch
  input goes back over a WebRTC data channel.

The Pi reports its display size, and SimHub renders at exactly that size, so the Pi never scales.

> **Status: prototype.** It's been tested end to end against SimHub 9.13.7 under Wine. Real Pi
> hardware is **not tested yet**: the Pi half has only run on a Linux desktop with software decoding.

Downloads: [latest release](https://github.com/giantorth/piscreen/releases/latest) — `PiScreen-driver.zip` (Windows) and
`piscreen-pi.tar.gz` (Raspberry Pi).

---

## 1. Raspberry Pi setup

**You need** a Pi Zero W / Zero 2 W (or any Pi) with a working display, on the same LAN as the
SimHub PC.

1. **Flash Raspberry Pi OS Lite** with Raspberry Pi Imager. A Zero / Zero W needs the
   **32-bit** image. In Imager's settings:
   - Hostname `piscreen`. The PC finds the Pi at `piscreen.local`.
   - Wi-Fi network and country.
   - Enable SSH.

2. **Get the panel working on the console first.** Follow the display maker's instructions
   (`dtoverlay=…` in `/boot/firmware/config.txt`, then reboot) until the Linux boot text shows on
   the panel. If the console shows up there, the receiver will too.

3. **Install the receiver.** SSH in (`ssh <user>@piscreen.local`) and run:
   ```bash
   curl -fsSL https://github.com/giantorth/piscreen/releases/latest/download/piscreen-pi.tar.gz | tar xz
   cd piscreen-pi-* && sudo ./install.sh
   ```
   From a checkout of this repo, `scp -r pi/ <user>@piscreen.local:~/piscreen` and running
   `sudo ./install.sh` in it does the same.
   This installs the GStreamer packages and puts the receiver in `/opt/piscreen`. It also enables the
   `piscreen` service, which listens on TCP port 8090.

4. **Check it:**
   ```bash
   curl http://piscreen.local:8090/info
   # {"name": "piscreen", "width": 800, "height": 480, "touch": true, "version": "0.1.0"}
   ```
   If the size or touch is wrong, run the probe on the Pi and look at what it reports:
   ```bash
   /opt/piscreen/probe.sh                       # display, touch, decoder, GStreamer check
   sudo systemctl stop piscreen && sudo /opt/piscreen/probe.sh --display-test   # colour bars on the panel
   ```
   Then fix it in `/etc/piscreen.conf` (size, sink, touch orientation and calibration) and run
   `sudo systemctl restart piscreen`.

**Optional:** to hide the console behind the dashboard, run `sudo systemctl disable --now getty@tty1`
and add `consoleblank=0 vt.global_cursor_default=0` to `/boot/firmware/cmdline.txt`.

## 2. Windows PC setup (SimHub)

**You need** SimHub 9.13 or newer, which added Device Builder MMF screens with external drivers.

1. **Install the driver.** Download `PiScreen-driver.zip` from the
   [latest release](https://github.com/giantorth/piscreen/releases/latest), unzip it, and in the unzipped folder run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```
   It finds SimHub, asks for admin rights and copies the driver to
   `C:\Program Files (x86)\SimHub\SharedDrivers\PiScreen\`. If SimHub is somewhere else, add
   `-SimHubPath 'D:\SimHub'`. To install by hand, copy the `PiScreen` folder there and *unblock* the
   files (right-click → Properties → Unblock).

   On first run the driver downloads Cisco's OpenH264 encoder (about 450 KB) from
   `ciscobinary.openh264.org`, checks its SHA-256 and stores it in `%LOCALAPPDATA%\PiScreen`.
   On a PC without internet access, download `openh264-2.6.0-win64.dll.bz2` from there yourself,
   decompress it and put the DLL in that folder.

2. **Create the device in SimHub:** Devices → Add new device → Device Builder → new device, e.g.
   "PiScreen".
   - Add a **Screen** feature with type **MMF Rendering (External process consumer)**.
   - Set **Display driver** to **Shared (SimHub\SharedDrivers)** and driver **PiScreen** (click
     refresh if the list is empty).
   - Save and add the device. A screen named **PiScreen 800x480** (or your panel's size) appears.

3. **Point it at the Pi.** The default is `piscreen.local:8090`. To change it, click the screen's
   **configure** button in SimHub. That opens the PiScreen settings: Pi host or IP, port, bitrate,
   max FPS, keyframe interval and **touch mode**, plus a *Test connection* button.

   **Touch mode:** SimHub has two ways of handling touch. *Simplified* treats each half of the
   screen as previous/next screen. *Advanced* presses the dashboard button under your finger. By
   default the driver leaves the choice to SimHub (device setting, or the dashboard's preferred
   mode). Choose **Press dashboard buttons** to force button presses for every dashboard.

4. **Pick a dashboard** for the screen in SimHub. It shows up on the Pi within a second or two.

Settings are stored in `%LOCALAPPDATA%\PiScreen\settings.ini`; the log is `driver.log` in the same
folder.

## 3. Linux + Wine (development)

SimHub under Wine works the same way. The only difference is that the driver goes into the
prefix's SimHub folder. The scripts default to
linux-simracing-utils (`~/linux-simracing-utils`). Override the paths with
`SIMHUB_PREFIX` / `SIMHUB_WINE`, or pass the SimHub directory to `install-driver.sh`.

```bash
./scripts/install-driver.sh            # fetch OpenH264, build, copy to <prefix>/…/SimHub/SharedDrivers/PiScreen
```
Then follow steps 2–4 of the Windows setup inside SimHub.

To test **without a Pi**, run the receiver on the desktop. The video shows in a window, and clicking
in the window acts as touch:
```bash
python3 pi/piscreen_receiver.py --config /dev/null --width 800 --height 480 --sink desktop --touch-device none
```
Then set `host=127.0.0.1` in the driver settings: SimHub's configure button, or
`<prefix>/drive_c/users/<you>/AppData/Local/PiScreen/settings.ini`.

## 4. Building

Requires the .NET SDK (8 or newer). The driver targets .NET Framework 4.8 / x64 and builds on
Windows, Linux or macOS.

```bash
./scripts/package-driver.sh     # → dist/PiScreen-driver.zip + dist/piscreen-pi.tar.gz (release assets)
```
On Windows without bash, run `dotnet build windows/PiScreen.Driver -c Release` and copy
`bin/Release` to `SharedDrivers\PiScreen`.

**OpenH264:** the encoder is Cisco's prebuilt binary. Neither the repo nor the release zip contains
it, because per openh264.org Cisco covers the H.264 patent royalties only for copies downloaded
from Cisco. The driver fetches it from Cisco on first run (pinned to 2.6.0 and SHA-256 verified).
`scripts/fetch-openh264.sh` gets a copy for local builds; a DLL next to `DisplayDriver.exe` takes
precedence.

## License

GPL-3.0, see [LICENSE](LICENSE).

## 5. Troubleshooting

| Symptom | Where to look |
|---|---|
| No PiScreen screen in SimHub | The driver isn't in `SharedDrivers\PiScreen`, or the device uses the wrong driver. Check `%LOCALAPPDATA%\PiScreen\driver.log`. |
| `Pi … not reachable` in driver.log | Is the Pi on? Check `curl http://piscreen.local:8090/info` from the PC. `.local` names need mDNS; use the IP in settings if it doesn't resolve. |
| Connects, but the screen stays black | On the Pi: `journalctl -u piscreen -f`. A "First frame decoded" line means the problem is on the display side, so run `probe.sh --display-test`. Otherwise try `decoder = avdec_h264`. |
| Picture smears or stutters on Wi-Fi | Lower the bitrate in the driver settings and raise `latency` in `/etc/piscreen.conf`. |
| Taps switch screens instead of pressing buttons | SimHub is in simplified touch mode. Set the driver's touch mode to *Press dashboard buttons*. |
| Touch lands in the wrong place | Add `touch_traces=1` to the driver's `settings.ini`: SimHub then draws a marker where it sees each touch. If the markers are off, set `touch_swap_xy`, `touch_invert_x`/`_y` or `touch_calibration` in `/etc/piscreen.conf`. |
| Want to see what the Pi is showing | `curl -o frame.png http://piscreen.local:8090/snapshot` saves the next decoded frame. |

## 6. Repository layout

| Path | What |
|---|---|
| `windows/PiScreen.Driver/` | `DisplayDriver.exe`: SimHub MMF V3 protocol (`Mmf/`), OpenH264 binding, first-run download and colour conversion (`Video/`), WebRTC and signaling (`Net/`) |
| `windows/package/install.ps1` | Windows installer script shipped in the zip |
| `windows/tools/FakeSimHub/` | Test harness that plays SimHub's side of the MMF protocol |
| `windows/tests/EncoderTest/` | Native smoke test for the OpenH264 binding (Linux or Windows) |
| `pi/` | Receiver, systemd unit, config, installer, hardware probe |
| `scripts/` | fetch-openh264, install-driver (Wine), package-driver, wine-run |
| `docs/architecture.md` | Design, protocols, SimHub MMF findings, limitations |
