using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PiScreen.Mmf;
using PiScreen.Net;
using PiScreen.Video;

namespace PiScreen
{
    /// <summary>
    /// Owns one SimHub screen ⇄ one Pi. A single polling thread drives the MMF channel, the encoder and
    /// link lifecycle; SIPSorcery callbacks only enqueue work for it.
    /// </summary>
    public sealed class DriverHost : IDisposable
    {
        public const string ScreenId = "piscreen-1"; // stable: SimHub attaches device instances to it

        private readonly string family;
        private readonly string indexName;
        private readonly SynchronizationContext ui;
        private string openH264Path;
        private Task<string> openH264Download;
        private DateTime nextOpenH264Download;
        private volatile Settings settings;
        private volatile bool settingsChanged;

        private DeviceIndex index;
        private ScreenChannel channel;
        private byte[] frame;
        private bool haveFrame, frameDirty;
        private OpenH264Encoder encoder;

        private PiClient client;
        private Task<PiInfo> infoTask;
        private PiInfo piInfo;
        private DateTime nextInfoAttempt;
        private int infoFailures;

        private WebRtcLink link;
        private Task connectTask;
        private DateTime nextConnectAttempt;
        private int connectFailures;
        private volatile bool linkClosed;
        private volatile bool controlOpened;

        private readonly ConcurrentQueue<string> inbox = new ConcurrentQueue<string>();
        private volatile bool keyFrameRequested;
        private readonly TouchContact[] touch = new TouchContact[4];
        private DateTime lastKeyRequestLog;
        private int lastTouchCount;
        private string channelTouchMode;
        private bool channelTouchTraces;

        private bool lastSimHubConnected;
        private int lastBrightness = -1;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long lastSentTicks, lastKeyTicks, lastStatsTicks;
        private long framesSent, bytesSent;

        private Thread thread;
        private volatile bool running;

        public DriverHost(string indexMmfName, Settings settings, SynchronizationContext ui)
        {
            indexName = indexMmfName;
            family = MmfNames.FamilyFromIndexName(indexMmfName);
            this.settings = settings;
            this.ui = ui;
            openH264Path = OpenH264Provider.FindExisting(AppDomain.CurrentDomain.BaseDirectory);
        }

        public void Start()
        {
            Log.Info($"Driver for family {family}, {settings}");
            index = new DeviceIndex(indexName);
            OpenChannel(settings.LastWidth, settings.LastHeight);
            client = new PiClient(settings.Host, settings.Port);
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "PiScreen loop", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        public void UpdateSettings(Settings s)
        {
            settings = s;
            settingsChanged = true;
        }

        private void Loop()
        {
            Native.timeBeginPeriod(1); // Windows sleeps in 15.6 ms steps otherwise; harmless under Wine
            try
            {
                while (running)
                {
                    try { Tick(); }
                    catch (Exception ex) { Log.Error("Loop: " + ex); Thread.Sleep(500); }
                    Thread.Sleep(3);
                }
            }
            finally
            {
                Native.timeEndPeriod(1);
            }
        }

        private void Tick()
        {
            var now = DateTime.UtcNow;
            var s = settings;

            if (settingsChanged)
            {
                settingsChanged = false;
                Log.Info("Settings changed: " + s);
                s.Save();
                client.Dispose();
                client = new PiClient(s.Host, s.Port);
                DropLink("settings changed");
                encoder?.Dispose();
                encoder = null;
                if (s.TouchMode != channelTouchMode || s.TouchTraces != channelTouchTraces)
                    OpenChannel(channel.Width, channel.Height); // SimHub only reads touch options on connect
            }

            // ---- encoder library (first run: fetched from Cisco in the background) ----
            if (openH264Path == null)
            {
                if (openH264Download == null && now >= nextOpenH264Download)
                    openH264Download = Task.Run(OpenH264Provider.DownloadAsync);
                else if (openH264Download != null && openH264Download.IsCompleted)
                {
                    openH264Path = openH264Download.Status == TaskStatus.RanToCompletion ? openH264Download.Result : null;
                    openH264Download = null;
                    if (openH264Path == null)
                    {
                        Log.Error("No OpenH264 library: video is off until the download works. Put " + OpenH264Provider.FileName +
                                  " (from ciscobinary.openh264.org) in " + Settings.Directory + " to install it by hand.");
                        nextOpenH264Download = now + TimeSpan.FromMinutes(1);
                    }
                }
            }

            // ---- SimHub side ----
            if (channel.Poll(frame))
            {
                haveFrame = true;
                frameDirty = true;
            }
            if (channel.TakeConfigurationRequest(out IntPtr owner))
                ui.Post(_ => ShowSettings(owner), null);

            if (channel.Connected != lastSimHubConnected)
            {
                lastSimHubConnected = channel.Connected;
                Log.Info("SimHub " + (lastSimHubConnected ? "rendering" : "not rendering"));
                SendState();
                if (lastSimHubConnected) keyFrameRequested = true;
            }
            if (channel.Connected && channel.Brightness != lastBrightness)
            {
                lastBrightness = channel.Brightness;
                link?.SendMessage(Json.Write(new BrightnessMessage { Value = lastBrightness }));
            }

            // ---- Pi side ----
            PollInfo(now, s);
            PollLink(now);

            if (controlOpened)
            {
                // The Pi only learns SimHub's state/brightness once the channel is up.
                controlOpened = false;
                SendState();
                lastBrightness = -1;
            }
            while (inbox.TryDequeue(out string msg)) HandleMessage(msg);

            // ---- video ----
            if (link != null && link.MediaReady && haveFrame && openH264Path != null)
            {
                long ticks = clock.ElapsedTicks;
                double sinceSent = (ticks - lastSentTicks) / (double)Stopwatch.Frequency;
                double sinceKey = (ticks - lastKeyTicks) / (double)Stopwatch.Frequency;
                bool periodicKey = s.KeyFrameIntervalSeconds > 0 && sinceKey >= s.KeyFrameIntervalSeconds;
                bool due = (frameDirty && sinceSent >= 1.0 / s.MaxFps) || keyFrameRequested || (periodicKey && frameDirty);
                if (due) EncodeAndSend(ticks, sinceSent, keyFrameRequested || periodicKey, s);
            }

            if ((clock.ElapsedTicks - lastStatsTicks) > Stopwatch.Frequency * 30)
            {
                lastStatsTicks = clock.ElapsedTicks;
                if (framesSent > 0)
                    Log.Info($"Stats: {framesSent} frames, {bytesSent / 1024} KiB in 30 s, torn reads {channel.TornFrames}");
                framesSent = bytesSent = 0;
            }
        }

        private void EncodeAndSend(long ticks, double sinceSent, bool key, Settings s)
        {
            if (encoder == null || encoder.Width != channel.Width || encoder.Height != channel.Height)
            {
                encoder?.Dispose();
                encoder = new OpenH264Encoder(openH264Path, channel.Width, channel.Height, s.BitrateKbps * 1000, s.MaxFps);
                Log.Info($"OpenH264 {encoder.Version} encoder {channel.Width}x{channel.Height} @ {s.BitrateKbps} kbps");
                key = true; // first frame of a new encoder is an IDR anyway
            }
            if (key)
            {
                encoder.ForceKeyFrame();
                keyFrameRequested = false;
            }

            long ms = ticks * 1000 / Stopwatch.Frequency;
            var au = encoder.EncodeBgra(frame, ms, out bool isKey);
            frameDirty = false;
            if (au == null) return;

            // RTP clock advances by the real gap since the last frame (frames are irregular: SimHub only renders changes).
            uint duration = lastSentTicks == 0 ? 3000u : (uint)Math.Max(1, Math.Min(90000, sinceSent * 90000));
            link.SendFrame(au.Value, duration);
            lastSentTicks = ticks;
            if (isKey) lastKeyTicks = ticks;
            framesSent++;
            bytesSent += au.Value.Count;
        }

        private void PollInfo(DateTime now, Settings s)
        {
            if (piInfo == null && infoTask == null && now >= nextInfoAttempt)
                infoTask = client.GetInfoAsync(CancellationToken.None);

            if (infoTask == null || !infoTask.IsCompleted) return;
            var t = infoTask;
            infoTask = null;
            if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
            {
                piInfo = t.Result;
                infoFailures = 0;
                Log.Info($"Pi {client.BaseUrl}: {piInfo.Name} {piInfo.Width}x{piInfo.Height} touch={piInfo.Touch} v{piInfo.Version}");
                int w = Even(Clamp(piInfo.Width, 64, 1920)), h = Even(Clamp(piInfo.Height, 64, 1080));
                if (w != channel.Width || h != channel.Height)
                {
                    Log.Info($"Render size {channel.Width}x{channel.Height} → {w}x{h} to match the Pi display");
                    var ns = s.Clone();
                    ns.LastWidth = w;
                    ns.LastHeight = h;
                    ns.Save();
                    settings = ns;
                    DropLink("render size changed");
                    OpenChannel(w, h);
                }
            }
            else
            {
                if (infoFailures++ % 10 == 0)
                    Log.Warn($"Pi {client.BaseUrl} not reachable: {Inner(t.Exception)}");
                nextInfoAttempt = now + TimeSpan.FromSeconds(3);
            }
        }

        private void PollLink(DateTime now)
        {
            if (linkClosed)
            {
                linkClosed = false;
                DropLink("closed");
                piInfo = null; // re-read the Pi's display before reconnecting
                nextConnectAttempt = now + TimeSpan.FromSeconds(1);
            }

            if (link == null && piInfo != null && now >= nextConnectAttempt)
            {
                var l = new WebRtcLink();
                l.MessageReceived += m => inbox.Enqueue(m);
                l.KeyFrameRequested += () => keyFrameRequested = true;
                l.Closed += why => { if (ReferenceEquals(link, l)) linkClosed = true; };
                l.ControlOpened += () => { if (ReferenceEquals(link, l)) controlOpened = true; };
                link = l;
                Log.Info("Connecting WebRTC to " + client.BaseUrl);
                connectTask = l.ConnectAsync(client, TimeSpan.FromSeconds(10), CancellationToken.None);
            }

            if (connectTask != null && connectTask.IsCompleted)
            {
                var t = connectTask;
                connectTask = null;
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    connectFailures = 0;
                    Log.Info("WebRTC connected");
                    keyFrameRequested = true;
                }
                else
                {
                    Log.Warn("WebRTC connect failed: " + Inner(t.Exception));
                    DropLink("connect failed");
                    piInfo = null;
                    connectFailures++;
                    nextConnectAttempt = now + TimeSpan.FromSeconds(Math.Min(10, 1 << Math.Min(connectFailures, 4)));
                }
            }
        }

        private void HandleMessage(string json)
        {
            try
            {
                var type = Json.Read<ControlMessage>(json).Type;
                switch (type)
                {
                    case "touch":
                        var m = Json.Read<TouchMessage>(json);
                        int n = Math.Min(4, m.Points?.Length ?? 0);
                        for (int i = 0; i < n; i++)
                        {
                            // Pixel under the normalized point; SetTouch clamps 1.0 to the last column/row.
                            touch[i].X = (int)Math.Floor(m.Points[i].X * channel.Width);
                            touch[i].Y = (int)Math.Floor(m.Points[i].Y * channel.Height);
                        }
                        channel.SetTouch(touch, n);
                        if (n != lastTouchCount)
                        {
                            // Log press/release transitions only; moves would flood the log.
                            Log.Info(n == 0 ? "Touch released" :
                                $"Touch {n} contact(s), first ({m.Points[0].X:F3},{m.Points[0].Y:F3}) -> pixel ({touch[0].X},{touch[0].Y})");
                            lastTouchCount = n;
                        }
                        break;
                    case "keyframe":
                        keyFrameRequested = true;
                        if ((DateTime.UtcNow - lastKeyRequestLog).TotalSeconds >= 5)
                        {
                            lastKeyRequestLog = DateTime.UtcNow;
                            Log.Info("Keyframe requested by the Pi (data channel)");
                        }
                        break;
                    default:
                        Log.Warn("Unknown control message: " + json);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Bad control message '" + json + "': " + ex.Message);
            }
        }

        private void SendState() =>
            link?.SendMessage(Json.Write(new StateMessage { SimHub = channel.Connected, Width = channel.Width, Height = channel.Height }));

        private void OpenChannel(int width, int height)
        {
            channel?.Dispose();
            var rnd = new Random();
            int id1 = rnd.Next(1, int.MaxValue), id2 = rnd.Next(1, int.MaxValue);
            channel = new ScreenChannel(family, id1, id2, width, height, ScreenChannel.CapabilityConfiguration,
                                       settings.MmfTouchMode, settings.TouchTraces);
            channelTouchMode = settings.TouchMode;
            channelTouchTraces = settings.TouchTraces;
            frame = new byte[width * height * 4];
            haveFrame = frameDirty = false;
            lastSimHubConnected = false;
            index.Publish(new[]
            {
                new PublishedScreen
                {
                    DeviceId = ScreenId,
                    DeviceInfo = $"PiScreen {width}x{height}",
                    Identifier1 = id1,
                    Identifier2 = id2,
                    Available = true,
                }
            });
            ScreenChannel.NotifySimHub();
            Log.Info($"Published screen {ScreenId} {width}x{height} ({MmfNames.Render(family, id1, id2)})");
        }

        private void DropLink(string why)
        {
            if (link == null) return;
            Log.Info("Dropping WebRTC link: " + why);
            var l = link;
            link = null;
            connectTask = null;
            l.Dispose();
        }

        private void ShowSettings(IntPtr owner)
        {
            using (var form = new SettingsForm(settings))
            {
                var result = owner != IntPtr.Zero ? form.ShowDialog(new Win32Owner(owner)) : form.ShowDialog();
                if (result == DialogResult.OK && form.Result != null) UpdateSettings(form.Result);
            }
        }

        private static string Inner(Exception ex)
        {
            while (ex is AggregateException a && a.InnerException != null) ex = a.InnerException;
            while (ex?.InnerException != null && !(ex is IOException)) ex = ex.InnerException;
            return ex?.Message ?? "unknown error";
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        private static int Even(int v) => v & ~1;

        public void Dispose()
        {
            running = false;
            thread?.Join(2000);
            DropLink("shutdown");
            encoder?.Dispose();
            channel?.Dispose();
            index?.Dispose();
            client?.Dispose();
        }
    }

    internal static class Native
    {
        [System.Runtime.InteropServices.DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [System.Runtime.InteropServices.DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int handle);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern int GetFileType(IntPtr handle);
    }
}
