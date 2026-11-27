using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using PiScreen.Net;

namespace PiScreen
{
    /// <summary>Opened from SimHub's "configure" button on the device (ConfigurationRequestCount).</summary>
    public sealed class SettingsForm : Form
    {
        private readonly TextBox host = new TextBox { Width = 220 };
        private readonly NumericUpDown port = Num(1, 65535);
        private readonly NumericUpDown bitrate = Num(200, 50000);
        private readonly NumericUpDown fps = Num(1, 120);
        private readonly NumericUpDown keyInterval = Num(0, 600);
        private readonly ComboBox touchMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        private static readonly string[] TouchModeKeys = { "simhub", "buttons", "leftright" };
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(360, 0) };

        public Settings Result { get; private set; }

        public SettingsForm(Settings current)
        {
            Text = "PiScreen display";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(10);

            host.Text = current.Host;
            port.Value = current.Port;
            bitrate.Value = current.BitrateKbps;
            fps.Value = current.MaxFps;
            keyInterval.Value = current.KeyFrameIntervalSeconds;
            touchMode.Items.AddRange(new object[] { "SimHub's touch setting", "Press dashboard buttons", "Left/right half switches screens" });
            touchMode.SelectedIndex = Math.Max(0, Array.IndexOf(TouchModeKeys, current.TouchMode));

            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            AddRow(grid, "Pi host name or IP", host);
            AddRow(grid, "Pi port", port);
            AddRow(grid, "Bitrate (kbit/s)", bitrate);
            AddRow(grid, "Max frame rate", fps);
            AddRow(grid, "Keyframe every (s, 0 = off)", keyInterval);
            AddRow(grid, "Touch", touchMode);

            var test = new Button { Text = "Test connection", AutoSize = true };
            test.Click += async (s, e) =>
            {
                status.Text = "Connecting…";
                try
                {
                    using (var c = new PiClient(host.Text.Trim(), (int)port.Value))
                    {
                        var info = await c.GetInfoAsync(CancellationToken.None);
                        status.Text = $"OK: {info.Name} {info.Width}x{info.Height}, touch {(info.Touch ? "yes" : "no")}";
                    }
                }
                catch (Exception ex)
                {
                    status.Text = "Failed: " + (ex.InnerException?.Message ?? ex.Message);
                }
            };
            grid.Controls.Add(test, 0, grid.RowCount);
            grid.Controls.Add(status, 1, grid.RowCount);
            grid.RowCount++;

            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            grid.Controls.Add(buttons, 0, grid.RowCount);
            grid.SetColumnSpan(buttons, 2);
            grid.RowCount++;

            Controls.Add(grid);
            AcceptButton = ok;
            CancelButton = cancel;

            FormClosing += (s, e) =>
            {
                if (DialogResult != DialogResult.OK) return;
                var r = current.Clone();
                r.Host = host.Text.Trim().Length > 0 ? host.Text.Trim() : current.Host;
                r.Port = (int)port.Value;
                r.BitrateKbps = (int)bitrate.Value;
                r.MaxFps = (int)fps.Value;
                r.KeyFrameIntervalSeconds = (int)keyInterval.Value;
                r.TouchMode = TouchModeKeys[Math.Max(0, touchMode.SelectedIndex)];
                Result = r;
            };
        }

        private static NumericUpDown Num(int min, int max) => new NumericUpDown { Minimum = min, Maximum = max, Width = 100 };

        private static void AddRow(TableLayoutPanel grid, string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, grid.RowCount);
            grid.Controls.Add(control, 1, grid.RowCount);
            grid.RowCount++;
        }
    }

    /// <summary>Wraps SimHub's window handle so the dialog opens on top of it.</summary>
    internal sealed class Win32Owner : IWin32Window
    {
        public Win32Owner(IntPtr handle) { Handle = handle; }
        public IntPtr Handle { get; }
    }
}
