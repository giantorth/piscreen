using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using PiScreen.Mmf;

// Usage: FakeSimHub.exe -family <guid> [-fps 30] [-seconds 0]
// Connects to the first available screen in SimHubDashIndexV3<guid> exactly like SimHub's
// GenericMMFServiceV3 does, then renders an animated test dashboard into it and reports the
// driver's keep-alive, touch points and frame counts.
unsafe class Program
{
    static int Main(string[] args)
    {
        string family = Arg(args, "-family");
        if (family == null) { Console.Error.WriteLine("usage: FakeSimHub -family <guid> [-fps 30] [-seconds 0]"); return 2; }
        int fps = int.TryParse(Arg(args, "-fps"), out var f) ? f : 30;
        int seconds = int.TryParse(Arg(args, "-seconds"), out var s) ? s : 0;
        var total = Stopwatch.StartNew();

        Console.WriteLine($"sizeof MMFHeaderV3={sizeof(MMFHeaderV3)} DeviceEntry={sizeof(DeviceEntry)} ActionCommand={sizeof(ActionCommand)}");
        using (var indexMap = MemoryMappedFile.CreateOrOpen(MmfNames.Index(family), sizeof(MMFDeviceIndex) * 10, MemoryMappedFileAccess.ReadWrite))
        using (var indexView = indexMap.CreateViewAccessor(0, sizeof(MMFDeviceIndex), MemoryMappedFileAccess.ReadWrite))
        {
            byte* ip = null;
            indexView.SafeMemoryMappedViewHandle.AcquirePointer(ref ip);
            while (seconds == 0 || total.Elapsed.TotalSeconds < seconds)
            {
                DeviceEntry* dev = FindAvailable((DeviceEntry*)ip);
                if (dev == null) { Thread.Sleep(250); continue; }
                string id = dev->GetDeviceID();
                Console.WriteLine($"Found screen '{id}' info='{dev->GetDeviceInfo()}' ids={dev->MMFIdentifier1}/{dev->MMFIdentifier2}");
                RunScreen(family, dev, fps, seconds, total);
            }
            indexView.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        return 0;
    }

    static DeviceEntry* FindAvailable(DeviceEntry* first)
    {
        for (int i = 0; i < MMFDeviceIndex.MaxDevices; i++)
            if (first[i].Available) return first + i;
        return null;
    }

    static void RunScreen(string family, DeviceEntry* dev, int fps, int seconds, Stopwatch total)
    {
        int id1 = dev->MMFIdentifier1, id2 = dev->MMFIdentifier2;
        MemoryMappedFile map;
        try { map = MemoryMappedFile.OpenExisting(MmfNames.Render(family, id1, id2), MemoryMappedFileRights.ReadWrite); }
        catch (Exception ex) { Console.WriteLine("Render MMF not open yet: " + ex.Message); Thread.Sleep(500); return; }

        using (map)
        using (var view = map.CreateViewAccessor(0, sizeof(MMFHeaderV3), MemoryMappedFileAccess.ReadWrite))
        {
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            var h = (MMFHeaderV3*)p;
            if (!h->RequestActive || h->StructVersion != 3 || h->RequestedRenderWidthPixels <= 0)
            {
                Console.WriteLine("Request not active yet");
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                Thread.Sleep(500);
                return;
            }
            int w = h->RequestedRenderWidthPixels, ht = h->RequestedRenderHeightPixels;
            Console.WriteLine($"Connected: {w}x{ht} capabilities={h->Capabilities} readTimeout={h->ReadTimeout}");
            // Same resets as GenericMMFServiceV3.ConnectToScreen
            h->Cursor1 = h->Cursor2 = h->Cursor3 = h->Cursor4 = default(MMFTouchPoint);
            h->DisplayBufferCurrentlyWritten = 0;
            h->DisplayLastFilledBuffer = 0;
            h->GlobalWriteCount = 0;
            h->DisplayWriteCount = 0;
            h->DisplayBrightness = 80;
            h->SimHubConnected = true;

            using (var bmp = new Bitmap(w, ht, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            using (var big = new Font(FontFamily.GenericSansSerif, ht / 5f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var small = new Font(FontFamily.GenericSansSerif, ht / 16f, GraphicsUnit.Pixel))
            {
                var frameClock = Stopwatch.StartNew();
                int frame = 0, lastRead = h->ReadCount;
                var lastReadChange = Stopwatch.StartNew();
                string lastTouch = "";
                long nextStats = 5000;
                while (seconds == 0 || total.Elapsed.TotalSeconds < seconds)
                {
                    // Driver gone or republished (new ids / size)?
                    if (!dev->Available || dev->MMFIdentifier1 != id1 || dev->MMFIdentifier2 != id2 || !h->RequestActive)
                    { Console.WriteLine("Screen withdrawn by driver"); break; }
                    if (h->ReadCount != lastRead) { lastRead = h->ReadCount; lastReadChange.Restart(); }
                    if (lastReadChange.Elapsed.TotalSeconds > (h->ReadTimeout == 0 ? 5 : h->ReadTimeout))
                    { Console.WriteLine("Driver stopped reading (ReadCount stale)"); break; }

                    Draw(g, w, ht, frame, big, small);
                    WriteFrame(h, bmp);
                    frame++;

                    string t = Touch(h);
                    if (t != lastTouch) { Console.WriteLine("Touch: " + (t.Length == 0 ? "released" : t)); lastTouch = t; }
                    if (frameClock.ElapsedMilliseconds > nextStats) { Console.WriteLine($"{frame} frames, driver ReadCount={h->ReadCount}"); nextStats += 5000; }

                    long due = (long)(frame * 1000.0 / fps);
                    int sleep = (int)(due - frameClock.ElapsedMilliseconds);
                    if (sleep > 0) Thread.Sleep(sleep);
                }
            }
            h->SimHubConnected = false;
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    // SimHub's BeginDraw/EndDraw: pick the buffer not last filled, bump its UpdateCount, copy, publish.
    static void WriteFrame(MMFHeaderV3* h, Bitmap bmp)
    {
        int buffer = h->DisplayLastFilledBuffer == 1 && h->RequestDoubleBuffer ? 2 : 1;
        h->DisplayBufferCurrentlyWritten = buffer;
        RenderBuffer* rb = buffer == 2 ? &h->DisplayRenderBuffer2 : &h->DisplayRenderBuffer1;
        rb->UpdateCount++;
        h->DisplayRenderedDataSize = bmp.Width * bmp.Height * 4;
        h->DisplayBytesPerRow = bmp.Width * 4;
        h->DisplayRenderedWidthPixels = bmp.Width;
        h->DisplayRenderedHeightPixels = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        for (int y = 0; y < bmp.Height; y++)
            Buffer.MemoryCopy((byte*)data.Scan0 + y * data.Stride, rb->Buffer + y * bmp.Width * 4, bmp.Width * 4, bmp.Width * 4);
        bmp.UnlockBits(data);
        h->GlobalWriteCount++;
        h->DisplayWriteCount++;
        h->DisplayBufferCurrentlyWritten = 0;
        h->DisplayLastFilledBuffer = buffer;
    }

    static void Draw(Graphics g, int w, int h, int frame, Font big, Font small)
    {
        g.Clear(Color.FromArgb(10, 12, 20));
        // RPM bar sweeping, gear digit, frame counter
        float rpm = (float)((Math.Sin(frame / 30.0) + 1) / 2);
        var bar = new Rectangle(w / 20, h / 12, (int)((w - w / 10) * rpm), h / 10);
        using (var b = new SolidBrush(rpm > 0.85 ? Color.Red : rpm > 0.6 ? Color.Gold : Color.LimeGreen)) g.FillRectangle(b, bar);
        g.DrawRectangle(Pens.White, w / 20, h / 12, w - w / 10, h / 10);
        g.DrawString(((frame / 60) % 6 + 1).ToString(), big, Brushes.White, w / 2f - big.Size / 3, h / 3f);
        g.DrawString($"FakeSimHub frame {frame}", small, Brushes.Cyan, w / 20f, h - small.Size * 2.2f);
        g.DrawString(DateTime.Now.ToString("HH:mm:ss.f"), small, Brushes.Orange, w * 0.65f, h - small.Size * 2.2f);
    }

    static string Touch(MMFHeaderV3* h)
    {
        string s = "";
        MMFTouchPoint* c = &h->Cursor1;
        for (int i = 0; i < 4; i++)
            if (c[i].CursorPressed) s += $"Cursor{i + 1}=({c[i].CursorCoordinatesX},{c[i].CursorCoordinatesY}) ";
        return s.Trim();
    }

    static string Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i < args.Length - 1 ? args[i + 1] : null;
    }
}
