using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using PiScreen.Mmf;

namespace PiScreen
{
    /// <summary>
    /// SimHub MMF screen driver ("DisplayDriver.exe"). SimHub starts it as
    ///   DisplayDriver.exe [-detect] -knowndevices &lt;definitionId&gt;,&lt;indexMmfName&gt;[,&lt;modelKey&gt;];...
    /// -detect: list the screen (not available) in the index and exit within SimHub's 5 s budget.
    /// Otherwise: run until stdin closes (SimHub's shutdown signal) or the process is killed.
    ///
    /// Manual/test use: DisplayDriver.exe -family &lt;guid&gt; [-host h] [-port p]
    /// </summary>
    public static class Program
    {
        private sealed class KnownDevice
        {
            public string DefinitionId;
            public string IndexMmfName;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            Log.Init(Settings.Directory);
            var known = ParseKnownDevices(args);
            string family = Arg(args, "-family");
            if (known.Count == 0 && family != null)
                known.Add(new KnownDevice { DefinitionId = "manual", IndexMmfName = MmfNames.Index(family) });

            if (known.Count == 0)
            {
                Log.Error("Usage: DisplayDriver.exe -knowndevices <definition id>,<index mmf name>[,<model key>];...  |  -family <guid>");
                return 1;
            }

            var settings = Settings.Load();
            string hostArg = Arg(args, "-host");
            if (hostArg != null) settings.Host = hostArg;
            if (int.TryParse(Arg(args, "-port"), out int port)) settings.Port = port;

            if (args.Contains("-detect"))
            {
                // No network here: SimHub kills detection after 5 s and reads the index we leave behind.
                foreach (var k in known)
                {
                    using (var index = new DeviceIndex(k.IndexMmfName) { ClearOnDispose = false })
                    {
                        index.Publish(new[]
                        {
                            new PublishedScreen { DeviceId = DriverHost.ScreenId, DeviceInfo = "PiScreen (" + settings.Host + ")", Available = false }
                        });
                    }
                    Log.Info("Detect: listed " + DriverHost.ScreenId + " for " + k.DefinitionId);
                }
                return 0;
            }

            if (known.Count > 1)
                Log.Warn($"{known.Count} device definitions use this driver; this prototype serves only the first ({known[0].DefinitionId}).");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var context = new ApplicationContext();
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var ui = SynchronizationContext.Current;

            Log.Info("PiScreen driver starting, args: " + string.Join(" ", args));
            var host = new DriverHost(known[0].IndexMmfName, settings, ui);
            host.Start();

            WatchStandardInput(() => ui.Post(_ => context.ExitThread(), null));
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; ui.Post(_ => context.ExitThread(), null); };

            Application.Run(context);
            Log.Info("PiScreen driver stopping");
            host.Dispose();
            return 0;
        }

        private static List<KnownDevice> ParseKnownDevices(string[] args)
        {
            var list = new List<KnownDevice>();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "-knowndevices") continue;
                foreach (var entry in args[i + 1].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = entry.Split(',');
                    if (parts.Length >= 2)
                        list.Add(new KnownDevice { DefinitionId = parts[0], IndexMmfName = parts[1] });
                }
            }
            return list;
        }

        private static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i < args.Length - 1 ? args[i + 1] : null;
        }

        /// <summary>SimHub keeps our stdin as a pipe and closes it to ask us to exit.</summary>
        private static void WatchStandardInput(Action onClosed)
        {
            const int StdInput = -10, FileTypePipe = 3;
            if (Native.GetFileType(Native.GetStdHandle(StdInput)) != FileTypePipe) return;
            new Thread(() =>
            {
                try { while (Console.In.ReadLine() != null) { } }
                catch { }
                Log.Info("stdin closed");
                onClosed();
            }) { IsBackground = true, Name = "stdin watch" }.Start();
        }
    }
}
