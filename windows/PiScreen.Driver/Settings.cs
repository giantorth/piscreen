using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PiScreen
{
    /// <summary>
    /// Driver settings, stored as key=value lines in %LOCALAPPDATA%\PiScreen\settings.ini.
    /// Immutable once built: the UI edits a copy and hands the new instance to the host.
    /// </summary>
    public sealed class Settings
    {
        public string Host { get; set; } = "piscreen.local";
        public int Port { get; set; } = 8090;
        public int BitrateKbps { get; set; } = 3000;
        public int MaxFps { get; set; } = 60;
        public int KeyFrameIntervalSeconds { get; set; } = 5;
        /// <summary>Ask SimHub to draw touch traces on the dashboard (debugging touch mapping).</summary>
        public bool TouchTraces { get; set; }
        /// <summary>simhub = use the screen's SimHub touch setting; buttons = touches press dashboard
        /// buttons; leftright = left/right half switches screens. SimHub reads it when it connects.</summary>
        public string TouchMode { get; set; } = "simhub";

        public Mmf.TouchMode MmfTouchMode =>
            TouchMode == "buttons" ? Mmf.TouchMode.ForceAdvanced :
            TouchMode == "leftright" ? Mmf.TouchMode.ForceLeftRight : Mmf.TouchMode.Unchanged;
        /// <summary>Last display size reported by the Pi; used until the Pi answers after a restart.</summary>
        public int LastWidth { get; set; } = 800;
        public int LastHeight { get; set; } = 480;

        public static string Directory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PiScreen");

        public static string FilePath => Path.Combine(Directory, "settings.ini");

        public Settings Clone() => (Settings)MemberwiseClone();

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "host": if (val.Length > 0) s.Host = val; break;
                        case "port": s.Port = Int(val, s.Port, 1, 65535); break;
                        case "bitrate_kbps": s.BitrateKbps = Int(val, s.BitrateKbps, 200, 50000); break;
                        case "max_fps": s.MaxFps = Int(val, s.MaxFps, 1, 120); break;
                        case "keyframe_interval_s": s.KeyFrameIntervalSeconds = Int(val, s.KeyFrameIntervalSeconds, 0, 600); break;
                        case "touch_mode":
                            string tm = val.ToLowerInvariant();
                            if (tm == "simhub" || tm == "buttons" || tm == "leftright") s.TouchMode = tm;
                            break;
                        case "touch_traces": s.TouchTraces = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "last_width": s.LastWidth = Int(val, s.LastWidth, 64, 1920); break;
                        case "last_height": s.LastHeight = Int(val, s.LastHeight, 64, 1080); break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Settings load: " + ex.Message);
            }
            return s;
        }

        public void Save()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var sb = new StringBuilder();
                sb.AppendLine("# PiScreen display driver settings");
                sb.AppendLine("host=" + Host);
                sb.AppendLine("port=" + Port.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("bitrate_kbps=" + BitrateKbps.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("max_fps=" + MaxFps.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("# 0 = only on connect / when the Pi asks for one");
                sb.AppendLine("keyframe_interval_s=" + KeyFrameIntervalSeconds.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("# simhub = SimHub's own touch setting, buttons = press dashboard buttons, leftright = halves switch screens");
                sb.AppendLine("touch_mode=" + TouchMode);
                sb.AppendLine("# 1 = SimHub draws a marker where it sees each touch (debugging)");
                sb.AppendLine("touch_traces=" + (TouchTraces ? "1" : "0"));
                sb.AppendLine("last_width=" + LastWidth.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("last_height=" + LastHeight.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch (Exception ex)
            {
                Log.Warn("Settings save: " + ex.Message);
            }
        }

        private static int Int(string s, int fallback, int min, int max) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? Math.Max(min, Math.Min(max, v)) : fallback;

        public override string ToString() =>
            $"host={Host}:{Port} bitrate={BitrateKbps}kbps maxfps={MaxFps} keyframe={KeyFrameIntervalSeconds}s touch={TouchMode}{(TouchTraces ? "+traces" : "")}";
    }
}
